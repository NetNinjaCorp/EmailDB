using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// What a <see cref="DisasterOpener"/> rebuild resolved to
/// (EmailDB_FileFormat_Spec.md Section 10.2 step 6, Section 13).
/// </summary>
public enum DisasterRecoveryKind
{
    /// <summary>
    /// Both superblock slots were invalid (severe damage, spec Section 13): the
    /// file was rebuilt from a full forward scan from offset 8192 — the block set,
    /// the BlockLocationIndex, a fresh Checkpoint, and a fresh superblock.
    /// </summary>
    RebuiltFromFullScan = 0,

    /// <summary>
    /// A valid superblock existed but no valid Checkpoint could be loaded (major
    /// damage, spec Section 13): every index was rebuilt from a full forward scan,
    /// a fresh Checkpoint written, and the superblock healed to it.
    /// </summary>
    RebuiltIndexFromScan = 1,
}

/// <summary>
/// The outcome of a <see cref="DisasterOpener"/> rebuild: the ready-to-use
/// <see cref="State"/> when the rebuilt file re-opened cleanly, plus the recovery
/// accounting the caller can log or assert on.
/// </summary>
public sealed class DisasterOpenResult
{
    /// <summary>Which disaster path ran (spec Section 10.2 step 6).</summary>
    public required DisasterRecoveryKind Kind { get; init; }

    /// <summary>
    /// The read-side open-state produced by re-opening the now-rebuilt file on the
    /// clean fast path. Null only when the rebuilt file could not be re-opened
    /// cleanly (e.g. an encrypted file with no bootstrap supplied); the rebuild
    /// itself still ran and <see cref="Detail"/> explains why no state is returned.
    /// </summary>
    public OpenState? State { get; init; }

    /// <summary>Distinct live BlockIds folded into the rebuilt BlockLocationIndex (post last-position-wins dedup).</summary>
    public required long RecoveredBlockCount { get; init; }

    /// <summary>Number of damaged byte ranges the full scan hunted across (spec Section 13).</summary>
    public required int DamagedRangeCount { get; init; }

    /// <summary>
    /// True when the full scan found at least one encrypted block but the key
    /// material (Salt / KdfParams / KeyVerificationToken) lived only in the lost
    /// superblock area: the encrypted payloads are UNRECOVERABLE without an external
    /// copy of that bootstrap (spec Section 13). Always false for the
    /// <see cref="DisasterRecoveryKind.RebuiltIndexFromScan"/> path (the superblock —
    /// hence the key material — survived).
    /// </summary>
    public required bool EncryptionUnrecoverable { get; init; }

    /// <summary>The FileId of the rebuilt superblock: recovered from a scanned Checkpoint, or freshly minted when none was found.</summary>
    public required byte[] FileId { get; init; }

    /// <summary>Human-readable context (encryption-unrecoverable warning, why no state, …); null when nothing noteworthy happened.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The disaster full-scan fallbacks (EmailDB_FileFormat_Spec.md Section 10.2 step 6,
/// Section 13): the last resort the <see cref="CleanOpener"/> / <see cref="DirtyOpener"/>
/// fast paths surface as failures and defer to. It handles the two catastrophic
/// cases the bounded paths refuse to improvise around:
/// <list type="number">
/// <item><b>No valid superblock</b> (both slots invalid — severe damage): a full
/// forward scan from offset 8192 rebuilds the block set and the BlockLocationIndex,
/// a fresh Checkpoint is written, and a fresh superblock is rebuilt from what the
/// scan found (FileId recovered from any surviving Checkpoint, else freshly minted).
/// The encryption bootstrap (Salt/KdfParams/KeyVerificationToken) lived only in the
/// lost superblock, so if any encrypted block is found, the rebuilt file cannot
/// decrypt it — this is surfaced explicitly via
/// <see cref="DisasterOpenResult.EncryptionUnrecoverable"/> rather than silently
/// producing an unreadable file.</item>
/// <item><b>No valid Checkpoint</b> (a valid superblock, but the commit chain is
/// gone — major damage): a full forward scan rebuilds the BlockLocationIndex, a
/// fresh Checkpoint commits it, and the superblock is healed to that commit point
/// (LastCheckpoint advanced, CleanShutdown = 1).</item>
/// </list>
///
/// <para>Both paths are O(file) and expensive by design — they exist only when the
/// bounded open paths cannot proceed. Every rebuild ends by re-running the
/// <see cref="CleanOpener"/> fast path over the healed file, so a returned
/// <see cref="DisasterOpenResult.State"/> is a genuine clean open of the rebuilt
/// file (proving the rebuild is coherent).</para>
///
/// <para>The BlockLocationIndex is the concrete index rebuilt here (spec Section 7:
/// derived data, always regenerable from a full scan). The folder tree and primary
/// index roots are not reconstructed — a disaster rebuild recovers block
/// addressability first; higher-level indexes are regenerated from the recovered
/// blocks by later machinery.</para>
/// </summary>
public static class DisasterOpener
{
    /// <summary>
    /// Runs the appropriate disaster fallback over <paramref name="stream"/> (spec
    /// Section 10.2 step 6). Dispatches on superblock validity: both slots invalid
    /// -> full-scan rebuild + superblock rebuild; a valid superblock -> full index
    /// rebuild + fresh Checkpoint + superblock heal. The caller owns
    /// <paramref name="stream"/>; a returned <see cref="DisasterOpenResult.State"/>
    /// owns only the block manager it created (dispose it to release that).
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream over the EmailDB file.</param>
    /// <param name="encryptionBootstrap">Encryption bootstrap seam forwarded to the final clean re-open (spec Section 10.2 step 2).</param>
    /// <param name="log">Optional sink for one message per damaged range during the full scan.</param>
    public static Result<DisasterOpenResult> Open(
        FileStream stream,
        IEncryptionBootstrap? encryptionBootstrap = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var superblockManager = new SuperblockManager(stream, ownsStream: false);
        var loaded = superblockManager.Load();
        return loaded.IsFailure
            ? RebuildWithoutSuperblock(stream, superblockManager, encryptionBootstrap, log)
            : RebuildWithoutCheckpoint(stream, superblockManager, loaded.Value, encryptionBootstrap, log);
    }

    // ---------------------------------------------- No valid superblock

    /// <summary>
    /// Disaster path 1 (spec Section 13, "Both slots invalid"): full scan from 8192
    /// rebuilds the block set + BlockLocationIndex, writes a fresh Checkpoint, and
    /// rebuilds the superblock. Encryption bootstrap is unrecoverable when only the
    /// lost superblock held it — surfaced explicitly.
    /// </summary>
    private static Result<DisasterOpenResult> RebuildWithoutSuperblock(
        FileStream stream,
        SuperblockManager superblockManager,
        IEncryptionBootstrap? encryptionBootstrap,
        Action<string>? log)
    {
        // A fresh default-bounded manager: without a superblock there is no
        // MaxPayloadLength to trust, so use the spec default (blocks past that bound
        // are treated as corrupt by the scan, spec Section 13).
        var manager = new BlockManager(stream, offsetMap: null, ownsStream: false);
        try
        {
            // Single full forward scan from 8192 that also recovers the FileId (from
            // any surviving Checkpoint) and notices encrypted blocks.
            var inspect = new RebuildScan(manager);
            var scan = manager.ScanForward(inspect, log);
            if (scan.IsFailure)
                return Result<DisasterOpenResult>.Failure(
                    $"Disaster rebuild failed: the full scan from 8192 hit an I/O error: {scan.Error}");

            byte[] fileId = inspect.RecoveredFileId ?? new UlidGenerator().Next();
            bool encryptionUnrecoverable = inspect.EncryptedBlockSeen;

            var index = BuildIndex(manager, inspect.Dedup);
            if (index.IsFailure)
                return Result<DisasterOpenResult>.Failure(index.Error);

            var checkpoint = WriteRebuiltCheckpoint(manager, fileId, index.Value);
            if (checkpoint.IsFailure)
                return Result<DisasterOpenResult>.Failure(checkpoint.Error);

            // Rebuild a fresh superblock pointing at the new Checkpoint. The lost
            // superblock held the encryption bootstrap, so the rebuilt one is
            // plaintext: encrypted blocks stay unreadable (flagged below), but the
            // file is at least openable and its blocks addressable again.
            var rebuilt = new Superblock
            {
                FileId = (byte[])fileId.Clone(),
                CreatedTimestamp = DateTime.UtcNow.Ticks,
                CleanShutdown = 1,
                MaxPayloadLength = manager.MaxPayloadLength,
                LastCheckpointBlockId = (byte[])checkpoint.Value.BlockId.Clone(),
                LastCheckpointOffset = checkpoint.Value.Offset,
                EncryptionEnabled = 0,
            };
            // Write BOTH slots so neither is a torn/garbage slot after the rebuild.
            var slotA = superblockManager.Write(rebuilt);
            if (slotA.IsFailure)
                return Result<DisasterOpenResult>.Failure(
                    $"Disaster rebuild recovered but writing the rebuilt superblock failed: {slotA.Error}");
            var slotB = superblockManager.Write(rebuilt.Clone());
            if (slotB.IsFailure)
                return Result<DisasterOpenResult>.Failure(
                    $"Disaster rebuild recovered but writing the second superblock slot failed: {slotB.Error}");

            string? detail = encryptionUnrecoverable
                ? "The file contained encrypted blocks, but the encryption bootstrap " +
                  "(Salt/KdfParams/KeyVerificationToken) lived only in the lost superblock and is UNRECOVERABLE " +
                  "without an external copy: the rebuilt file is addressable but its encrypted payloads cannot be " +
                  "decrypted (spec Section 13)."
                : null;

            return FinishWithCleanOpen(
                stream, manager, DisasterRecoveryKind.RebuiltFromFullScan,
                index.Value.Count, scan.Value.DamagedRanges.Count, encryptionUnrecoverable,
                fileId, detail, encryptionBootstrap);
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    // ---------------------------------------------- No valid Checkpoint

    /// <summary>
    /// Disaster path 2 (spec Section 13, "No valid Checkpoint"): a valid superblock
    /// survived, but no Checkpoint can be loaded — full scan rebuilds the
    /// BlockLocationIndex, a fresh Checkpoint commits it, and the superblock is
    /// healed to that commit point.
    /// </summary>
    private static Result<DisasterOpenResult> RebuildWithoutCheckpoint(
        FileStream stream,
        SuperblockManager superblockManager,
        Superblock superblock,
        IEncryptionBootstrap? encryptionBootstrap,
        Action<string>? log)
    {
        var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, offsetMap: null, ownsStream: false);
        try
        {
            var inspect = new RebuildScan(manager);
            var scan = manager.ScanForward(inspect, log);
            if (scan.IsFailure)
                return Result<DisasterOpenResult>.Failure(
                    $"Disaster index rebuild failed: the full scan hit an I/O error: {scan.Error}");

            var index = BuildIndex(manager, inspect.Dedup);
            if (index.IsFailure)
                return Result<DisasterOpenResult>.Failure(index.Error);

            var checkpoint = WriteRebuiltCheckpoint(manager, superblock.FileId, index.Value);
            if (checkpoint.IsFailure)
                return Result<DisasterOpenResult>.Failure(checkpoint.Error);

            // Heal the surviving superblock to the fresh commit point.
            var healed = superblock.Clone();
            healed.LastCheckpointBlockId = (byte[])checkpoint.Value.BlockId.Clone();
            healed.LastCheckpointOffset = checkpoint.Value.Offset;
            healed.CleanShutdown = 1;
            var written = superblockManager.Write(healed);
            if (written.IsFailure)
                return Result<DisasterOpenResult>.Failure(
                    $"Disaster index rebuild recovered but healing the superblock failed: {written.Error}");

            return FinishWithCleanOpen(
                stream, manager, DisasterRecoveryKind.RebuiltIndexFromScan,
                index.Value.Count, scan.Value.DamagedRanges.Count, encryptionUnrecoverable: false,
                (byte[])superblock.FileId.Clone(), detail: null, encryptionBootstrap);
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------- Shared steps

    /// <summary>
    /// Builds a fresh BlockLocationIndex over the file (same block stream) from the
    /// deduplicated set the scan collected (spec Section 7: the index is derived data
    /// rebuilt by a full scan). The index nodes are appended past EOF; the scan that
    /// produced <paramref name="dedup"/> already completed, so the new nodes are not
    /// part of the mapped set.
    /// </summary>
    private static Result<BlockLocationIndex> BuildIndex(BlockManager manager, RuntimeBlockOffsetMap dedup)
    {
        var nodeStore = new BTreeNodeStore(manager, blockIdResolver: null);
        var index = new BlockLocationIndex(nodeStore);
        var put = index.PutBatch(dedup.SnapshotOrderedByOffset());
        if (put.IsFailure)
            return Result<BlockLocationIndex>.Failure(
                $"Disaster rebuild failed batch-inserting {dedup.Count} scanned location(s): {put.Error}");
        // Make the rebuilt index nodes durable before the Checkpoint that names them.
        var flush = manager.Flush();
        if (flush.IsFailure)
            return Result<BlockLocationIndex>.Failure(
                $"Disaster rebuild failed making the rebuilt index nodes durable: {flush.Error}");
        return Result<BlockLocationIndex>.Success(index);
    }

    /// <summary>
    /// Writes a fresh first Checkpoint (sequence 0) naming the rebuilt
    /// BlockLocationIndex root — no folder/primary/metadata/keystore roots (a
    /// disaster rebuild recovers block addressability, not higher indexes).
    /// Returns the committed Checkpoint pointer.
    /// </summary>
    private static Result<CheckpointRootPointer> WriteRebuiltCheckpoint(
        BlockManager manager, byte[] fileId, BlockLocationIndex index)
    {
        CheckpointRootPointer locationPointer;
        if (index.Root is null)
        {
            locationPointer = CheckpointRootPointer.None;
        }
        else
        {
            long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root.RootRef.Reference);
            var rootBlock = manager.Read(rootOffset);
            if (rootBlock.IsFailure)
                return Result<CheckpointRootPointer>.Failure(
                    $"Disaster rebuild could not read the rebuilt location index root at offset {rootOffset}: {rootBlock.Error}");
            locationPointer = CheckpointRootPointer.Create(
                (byte[])rootBlock.Value.Header.BlockId.Clone(), rootOffset);
        }

        var writer = new CheckpointWriter(manager, fileId);
        var written = writer.WriteCheckpoint(new CheckpointContents
        {
            FolderTreeRoot = CheckpointRootPointer.None,
            PrimaryIndexRoot = CheckpointRootPointer.None,
            LocationIndexRoot = locationPointer,
            MetadataRoot = CheckpointRootPointer.None,
            KeyStoreRoot = CheckpointRootPointer.None,
            SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
            LiveBlockCount = index.Count,
            LiveByteCount = 0,
            DeadByteCount = 0,
        });
        if (written.IsFailure)
            return Result<CheckpointRootPointer>.Failure(
                $"Disaster rebuild could not write the fresh Checkpoint: {written.Error}");

        return Result<CheckpointRootPointer>.Success(writer.LastCheckpointPointer);
    }

    /// <summary>
    /// Disposes the rebuild manager and re-opens the now-healed file on the clean
    /// fast path, so the returned state is a genuine clean open of the rebuilt file.
    /// A non-clean outcome (e.g. an encrypted file with no bootstrap) is reported as
    /// a stateless success carrying the reason — the rebuild itself succeeded.
    /// </summary>
    private static Result<DisasterOpenResult> FinishWithCleanOpen(
        FileStream stream,
        BlockManager manager,
        DisasterRecoveryKind kind,
        long recoveredBlockCount,
        int damagedRangeCount,
        bool encryptionUnrecoverable,
        byte[] fileId,
        string? detail,
        IEncryptionBootstrap? encryptionBootstrap)
    {
        manager.Dispose();

        var clean = CleanOpener.Open(stream, encryptionBootstrap);
        if (clean.IsFailure)
            return Result<DisasterOpenResult>.Failure(
                $"Disaster rebuild healed the file but the clean re-open failed: {clean.Error}");

        OpenState? state = null;
        if (clean.Value.Kind == OpenOutcomeKind.CleanOpen)
        {
            state = clean.Value.State;
        }
        else
        {
            string reason = $"the rebuilt file re-opened as {clean.Value.Kind}: {clean.Value.Detail}";
            detail = detail is null ? reason : $"{detail} Additionally, {reason}";
        }

        return Result<DisasterOpenResult>.Success(new DisasterOpenResult
        {
            Kind = kind,
            State = state,
            RecoveredBlockCount = recoveredBlockCount,
            DamagedRangeCount = damagedRangeCount,
            EncryptionUnrecoverable = encryptionUnrecoverable,
            FileId = fileId,
            Detail = detail,
        });
    }

    /// <summary>
    /// A scan-time <see cref="IBlockOffsetMap"/> that, alongside collecting the
    /// deduplicated location set (last-position-wins via <see cref="RuntimeBlockOffsetMap"/>),
    /// recovers the FileId from the first Checkpoint it sees and notices any encrypted
    /// block — everything the superblock rebuild needs, gathered in the one full scan.
    /// </summary>
    private sealed class RebuildScan : IBlockOffsetMap
    {
        private readonly BlockManager _manager;

        public RebuildScan(BlockManager manager) => _manager = manager;

        /// <summary>The deduplicated live location set the rebuilt index is built from.</summary>
        public RuntimeBlockOffsetMap Dedup { get; } = new();

        /// <summary>True once any scanned block carried the Encrypted flag.</summary>
        public bool EncryptedBlockSeen { get; private set; }

        /// <summary>FileId recovered from the first valid Checkpoint found, or null if none.</summary>
        public byte[]? RecoveredFileId { get; private set; }

        public void OnBlockAppended(BlockHeader header, long offset, long totalBlockLength)
        {
            Dedup.OnBlockAppended(header, offset, totalBlockLength);

            if (header.IsEncrypted)
                EncryptedBlockSeen = true;

            if (RecoveredFileId is null && header.Type == BlockType.Checkpoint)
            {
                var block = _manager.Read(offset);
                if (block.IsSuccess)
                {
                    var checkpoint = CheckpointSerializer.Deserialize(block.Value.Payload);
                    if (checkpoint.IsSuccess)
                        RecoveredFileId = (byte[])checkpoint.Value.FileId.Clone();
                }
            }
        }
    }
}
