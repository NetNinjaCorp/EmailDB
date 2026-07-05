namespace EmailDB.Format.V3;

/// <summary>
/// Appends WAL blocks (BlockType 1, EmailDB_FileFormat_Spec.md Section 10.4) via
/// the standard append-only block machinery — no raw regions, no in-place
/// rewrites — retiring the v1 fixed-region WAL. Each block is stamped with the
/// CURRENT (last committed) Checkpoint's BlockId and the next monotonic
/// <see cref="WalSequence"/>, so recovery replays exactly the uncommitted
/// operations after the fenced Checkpoint (spec Section 10.4).
///
/// <para><b>Checkpoint fence.</b> The current Checkpoint pointer is read fresh on
/// every append from an injected supplier (typically
/// <see cref="CheckpointWriter.LastCheckpointPointer"/>), so a WAL block written
/// after Checkpoint N carries N's BlockId. A WAL block cannot be written before
/// the first Checkpoint exists: an absent (all-zero) pointer fails the append —
/// there is nothing to fence against.</para>
///
/// <para><b>Monotonic sequence.</b> The first WAL block of a file carries
/// <see cref="InitialSequence"/>; each subsequent block carries predecessor + 1.
/// The sequence advances only after a successful append, so a failed append
/// never burns a sequence number and never leaves a gap. Reopen an existing file
/// by seeding the last durable WAL sequence via the resume constructor.</para>
///
/// <para><b>Durability.</b> Like every append, the write is buffered; durability
/// comes from a subsequent <see cref="BlockManager.Flush"/> at the writer's
/// flush boundary (spec Section 12). A poisoned block stream (after a failed
/// write/fsync) fails the append without advancing state.</para>
///
/// Not thread-safe: runs on the single writer thread.
/// </summary>
public sealed class WalWriter
{
    /// <summary>Sequence stamped into the very first WAL block of a file.</summary>
    public const ulong InitialSequence = 0;

    private readonly BlockManager _manager;
    private readonly Func<CheckpointRootPointer> _currentCheckpoint;

    private bool _hasWritten;
    private ulong _lastSequence;

    /// <summary>
    /// Creates a writer for a fresh file whose first WAL block will carry
    /// <see cref="InitialSequence"/>.
    /// </summary>
    /// <param name="manager">The block manager whose append machinery writes the WAL block.</param>
    /// <param name="currentCheckpoint">
    /// Supplies the current (last committed) Checkpoint pointer, read fresh on
    /// every append; typically <c>() =&gt; checkpointWriter.LastCheckpointPointer</c>.
    /// </param>
    public WalWriter(BlockManager manager, Func<CheckpointRootPointer> currentCheckpoint)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _currentCheckpoint = currentCheckpoint ?? throw new ArgumentNullException(nameof(currentCheckpoint));
        _hasWritten = false;
    }

    /// <summary>
    /// Creates a writer resuming an existing file: the next WAL block carries
    /// <paramref name="lastSequence"/> + 1, so the sequence continues unbroken
    /// across a reopen.
    /// </summary>
    /// <param name="manager">The block manager whose append machinery writes the WAL block.</param>
    /// <param name="currentCheckpoint">Supplies the current Checkpoint pointer, read fresh on every append.</param>
    /// <param name="lastSequence">Sequence of the last durable WAL block loaded on open.</param>
    public WalWriter(BlockManager manager, Func<CheckpointRootPointer> currentCheckpoint, ulong lastSequence)
        : this(manager, currentCheckpoint)
    {
        _hasWritten = true;
        _lastSequence = lastSequence;
    }

    /// <summary>True once at least one WAL block has been appended (or resumed).</summary>
    public bool HasWritten => _hasWritten;

    /// <summary>Sequence of the most recently appended WAL block, or null before the first.</summary>
    public ulong? LastSequence => _hasWritten ? _lastSequence : null;

    /// <summary>
    /// Serializes <paramref name="entries"/> into a WAL block stamped with the
    /// current Checkpoint's BlockId and the next monotonic
    /// <see cref="WalSequence"/>, and appends it via the standard block machinery
    /// (buffered — flush at the writer's flush boundary to make it durable). The
    /// sequence advances only on a successful append.
    /// </summary>
    /// <param name="entries">The logged mutations for this block (may be empty).</param>
    /// <returns>The appended <see cref="WalBlock"/> and its on-disk location, or a failure with state unchanged.</returns>
    public Result<WalAppendResult> Append(IReadOnlyList<WalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (_manager.IsPoisoned)
            return Result<WalAppendResult>.Failure(
                "Block stream is poisoned after a failed write/fsync; close the file and run crash recovery (spec Section 10.3).");

        if (_hasWritten && _lastSequence == ulong.MaxValue)
            return Result<WalAppendResult>.Failure(
                "WalSequence has reached ulong.MaxValue; no further WAL blocks can be written.");

        CheckpointRootPointer? current = _currentCheckpoint();
        if (current is null || current.IsAbsent)
            return Result<WalAppendResult>.Failure(
                "WAL append aborted: no committed Checkpoint to fence against; write the first Checkpoint " +
                "before logging WAL blocks (spec Section 10.4).");

        ulong sequence = _hasWritten ? _lastSequence + 1 : InitialSequence;

        var wal = new WalBlock
        {
            WalSequence = sequence,
            CheckpointBlockId = (byte[])current.BlockId.Clone(),
            Entries = entries,
        };

        byte[] payload;
        try
        {
            payload = WalSerializer.Serialize(wal);
        }
        catch (ArgumentException ex)
        {
            return Result<WalAppendResult>.Failure(
                $"WAL append aborted: the entries form an invalid WAL payload: {ex.Message}");
        }

        var appended = _manager.Append(BlockType.WAL, PayloadEncoding.Custom, payload);
        if (appended.IsFailure)
            return Result<WalAppendResult>.Failure(
                $"WAL append aborted: appending the WAL block failed: {appended.Error}");

        // Commit: only now that the block is appended does the sequence advance.
        _hasWritten = true;
        _lastSequence = sequence;

        return Result<WalAppendResult>.Success(new WalAppendResult(wal, appended.Value));
    }
}

/// <summary>The outcome of a successful <see cref="WalWriter.Append"/>: the stamped WAL block and where it landed.</summary>
/// <param name="Wal">The WAL block as written, carrying the stamped sequence and Checkpoint fence.</param>
/// <param name="Location">The appended block's minted ULID, file offset, and on-disk length.</param>
public sealed record WalAppendResult(WalBlock Wal, BlockLocation Location);
