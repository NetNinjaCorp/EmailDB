namespace EmailDB.Format.V3;

/// <summary>
/// Writes the Checkpoint block — the single commit point of a v3 mutation batch
/// (EmailDB_FileFormat_Spec.md Sections 10.1, 10.3). At each commit the caller has
/// already appended the batch's content (data/node blocks and the index-root blocks
/// the Checkpoint will name) as buffered, not-yet-durable writes; this writer then
/// enforces the spec's durability ordering:
///
/// <code>
///   batch contents  ->  fsync  ->  Checkpoint block  ->  fsync
/// </code>
///
/// <para>The first fsync makes every block the Checkpoint references durable
/// BEFORE the Checkpoint that commits them exists; the Checkpoint is appended and a
/// second fsync makes the commit point itself durable. Only once that second fsync
/// returns does the Checkpoint count as committed — the writer advances its
/// sequence and previous-pointer state at that instant and not before, so a failure
/// at any earlier step leaves the previous Checkpoint authoritative.</para>
///
/// <para><b>Monotonic sequence and walkable chain.</b> Each Checkpoint is stamped
/// with <see cref="Checkpoint.CheckpointSequence"/> = predecessor + 1 (the first is
/// <see cref="InitialSequence"/>) and a <see cref="Checkpoint.PreviousCheckpoint"/>
/// pointer (ULID + offset) to the predecessor, so recovery can walk the chain
/// newest-to-oldest. The first Checkpoint of a file links
/// <see cref="CheckpointRootPointer.None"/>. Reopen an existing file by seeding the
/// last durable Checkpoint's sequence and pointer via the resume constructor.</para>
///
/// <para><b>Failure contract.</b> fsync failure is FATAL (spec Section 10.3): the
/// underlying <see cref="BlockManager"/>/<c>DurableStream</c> poisons the handle and
/// this writer surfaces the failure without advancing its state — the caller must
/// close the file and rely on crash recovery. A torn Checkpoint therefore never
/// becomes the committed sequence: it either is fully durable (both fsyncs
/// succeeded) or it never advanced the writer.</para>
///
/// Not thread-safe: runs at the single writer's Checkpoint commit point.
/// </summary>
public sealed class CheckpointWriter
{
    /// <summary>Sequence stamped into the very first Checkpoint of a file.</summary>
    public const ulong InitialSequence = 0;

    private readonly BlockManager _manager;
    private readonly byte[] _fileId;
    private readonly ushort _formatVersion;

    private bool _hasCheckpoint;
    private ulong _lastSequence;
    private CheckpointRootPointer _previous = CheckpointRootPointer.None;

    /// <summary>
    /// Creates a writer for a fresh file whose first Checkpoint will carry
    /// <see cref="InitialSequence"/> and link <see cref="CheckpointRootPointer.None"/>.
    /// </summary>
    /// <param name="manager">The block manager whose append/fsync machinery commits the Checkpoint.</param>
    /// <param name="fileId">The file's 16-byte ULID, stamped into and cross-checked against every Checkpoint.</param>
    /// <param name="formatVersion">Format version stamped into the payload (defaults to the current v3 version).</param>
    public CheckpointWriter(
        BlockManager manager,
        byte[] fileId,
        ushort formatVersion = BlockSerializer.CurrentFormatVersion)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(fileId);
        if (fileId.Length != Checkpoint.FileIdSize)
            throw new ArgumentException(
                $"{nameof(fileId)} must be exactly {Checkpoint.FileIdSize} bytes, got {fileId.Length}.",
                nameof(fileId));

        _manager = manager;
        _fileId = (byte[])fileId.Clone();
        _formatVersion = formatVersion;
        _hasCheckpoint = false;
    }

    /// <summary>
    /// Creates a writer resuming an existing file: the next Checkpoint carries
    /// <paramref name="lastSequence"/> + 1 and links <paramref name="lastCheckpoint"/>,
    /// so the sequence and chain continue unbroken across a reopen.
    /// </summary>
    /// <param name="manager">The block manager whose append/fsync machinery commits the Checkpoint.</param>
    /// <param name="fileId">The file's 16-byte ULID, stamped into and cross-checked against every Checkpoint.</param>
    /// <param name="lastSequence">Sequence of the last durable Checkpoint loaded on open.</param>
    /// <param name="lastCheckpoint">ULID+offset pointer to that last durable Checkpoint.</param>
    /// <param name="formatVersion">Format version stamped into the payload (defaults to the current v3 version).</param>
    public CheckpointWriter(
        BlockManager manager,
        byte[] fileId,
        ulong lastSequence,
        CheckpointRootPointer lastCheckpoint,
        ushort formatVersion = BlockSerializer.CurrentFormatVersion)
        : this(manager, fileId, formatVersion)
    {
        ArgumentNullException.ThrowIfNull(lastCheckpoint);
        if (lastCheckpoint.IsAbsent)
            throw new ArgumentException(
                "Resuming requires a real previous-Checkpoint pointer, not CheckpointRootPointer.None.",
                nameof(lastCheckpoint));

        _hasCheckpoint = true;
        _lastSequence = lastSequence;
        _previous = lastCheckpoint;
    }

    /// <summary>True once at least one Checkpoint has been durably written (or resumed).</summary>
    public bool HasCheckpoint => _hasCheckpoint;

    /// <summary>Sequence of the most recently committed Checkpoint, or null before the first.</summary>
    public ulong? LastSequence => _hasCheckpoint ? _lastSequence : null;

    /// <summary>
    /// ULID+offset pointer to the most recently committed Checkpoint — the value
    /// the next Checkpoint links as its <see cref="Checkpoint.PreviousCheckpoint"/>
    /// and the superblock records as its <c>LastCheckpoint</c> hint.
    /// <see cref="CheckpointRootPointer.None"/> before the first Checkpoint.
    /// </summary>
    public CheckpointRootPointer LastCheckpointPointer => _previous;

    /// <summary>
    /// Commits a mutation batch by writing its Checkpoint (spec Sections 10.1,
    /// 10.3): fsync the already-appended batch contents, append the Checkpoint
    /// naming <paramref name="contents"/>' roots with the next monotonic sequence
    /// and a link to the previous Checkpoint, then fsync so the commit point is
    /// durable. The writer's sequence/previous state advances only after that final
    /// fsync succeeds.
    /// </summary>
    /// <param name="contents">The batch's roots and live/dead accounting.</param>
    /// <returns>The committed Checkpoint on success; a failure (with the writer state unchanged) otherwise.</returns>
    public Result<Checkpoint> WriteCheckpoint(CheckpointContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (_manager.IsPoisoned)
            return Result<Checkpoint>.Failure(
                "Block stream is poisoned after a failed write/fsync; close the file and run crash recovery (spec Section 10.3).");

        if (_hasCheckpoint && _lastSequence == ulong.MaxValue)
            return Result<Checkpoint>.Failure(
                "CheckpointSequence has reached ulong.MaxValue; no further Checkpoints can be written.");

        ulong sequence = _hasCheckpoint ? _lastSequence + 1 : InitialSequence;

        // 1. fsync the batch contents (already appended, buffered) BEFORE the
        //    Checkpoint that commits them (spec Section 10.3). fsync failure is
        //    fatal: the manager is poisoned and no Checkpoint is written.
        var contentsFsync = _manager.Flush();
        if (contentsFsync.IsFailure)
            return Result<Checkpoint>.Failure(
                "Checkpoint aborted: fsync of the batch contents failed (fatal, spec Section 10.3); " +
                $"the Checkpoint is NOT committed and the previous one remains authoritative. {contentsFsync.Error}");

        var checkpoint = new Checkpoint
        {
            FormatVersion = _formatVersion,
            CheckpointSequence = sequence,
            FileId = (byte[])_fileId.Clone(),
            FolderTreeRoot = contents.FolderTreeRoot,
            PrimaryIndexRoot = contents.PrimaryIndexRoot,
            LocationIndexRoot = contents.LocationIndexRoot,
            MetadataRoot = contents.MetadataRoot,
            KeyStoreRoot = contents.KeyStoreRoot,
            PreviousCheckpoint = _previous,
            SecondaryIndexes = contents.SecondaryIndexes ?? Array.Empty<CheckpointSecondaryIndex>(),
            LiveBlockCount = contents.LiveBlockCount,
            LiveByteCount = contents.LiveByteCount,
            DeadByteCount = contents.DeadByteCount,
        };

        byte[] payload;
        try
        {
            payload = CheckpointSerializer.Serialize(checkpoint);
        }
        catch (ArgumentException ex)
        {
            return Result<Checkpoint>.Failure(
                $"Checkpoint aborted: the batch contents form an invalid Checkpoint payload: {ex.Message}");
        }

        // 2. Append the Checkpoint block (buffered).
        var appended = _manager.Append(BlockType.Checkpoint, PayloadEncoding.Custom, payload);
        if (appended.IsFailure)
            return Result<Checkpoint>.Failure(
                $"Checkpoint aborted: appending the Checkpoint block failed: {appended.Error}");

        // 3. fsync so the Checkpoint — the commit point — is durable (spec Section
        //    10.3). Until this returns the Checkpoint is not committed.
        var checkpointFsync = _manager.Flush();
        if (checkpointFsync.IsFailure)
            return Result<Checkpoint>.Failure(
                "Checkpoint aborted: fsync of the Checkpoint block failed (fatal, spec Section 10.3); " +
                $"the Checkpoint is NOT committed and the previous one remains authoritative. {checkpointFsync.Error}");

        // 4. Commit: only now that the Checkpoint is durable does the writer advance
        //    its monotonic sequence and previous-pointer chain link.
        _hasCheckpoint = true;
        _lastSequence = sequence;
        _previous = CheckpointRootPointer.Create(
            (byte[])appended.Value.BlockId.Clone(), appended.Value.Offset);

        return Result<Checkpoint>.Success(checkpoint);
    }
}
