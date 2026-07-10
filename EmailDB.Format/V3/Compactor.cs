using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// Level-1 full-file compaction — the side-file rewrite + atomic swap that is v3's
/// only space reclaimer (EmailDB_FileFormat_Spec.md Section 11.2,
/// docs/Compaction.md Section 2). An append-only file cannot reclaim interior dead
/// space in place, so compaction copies every live block into a fresh
/// <c>&lt;name&gt;.emdb.compact</c> side file — same <c>FileId</c>, continued
/// <c>SuperblockSequence</c> — then (in later slices) rebuilds the derived
/// BlockLocationIndex, writes a fresh Checkpoint, and atomically renames the side
/// file over the original. A crash at any point leaves either the complete old file
/// or the complete new file, never a hybrid.
///
/// <para><b>This class implements the first slice (US-EMDB-89-6): the live-block
/// walk and the verbatim side-file copy.</b> It opens a clean snapshot of the
/// source, creates the side file with fresh dual-slot superblocks that continue the
/// source's <c>SuperblockSequence</c> and carry the same <c>FileId</c>, and copies
/// every live block bit-for-bit — preserving each <see cref="CopiedBlock.BlockId"/>
/// and its exact bytes (spec Section 11.2: "No logical block content is rewritten —
/// every persisted pointer is a ULID, so blocks just move"). It records the
/// old→new offset of every copied block in <see cref="CopiedBlocks"/>.</para>
///
/// <para><b>The set of live blocks is the latest Checkpoint's BlockLocationIndex.</b>
/// That index maps <c>BlockId → (Offset, Length)</c> for exactly the live blocks
/// (its entry count is the Checkpoint's <see cref="Checkpoint.LiveBlockCount"/>, spec
/// Sections 7, 10.1), so walking it IS the "walk live roots, copy every reachable
/// block" of docs/Compaction.md Section 2 — no graph traversal of every block type
/// is needed, and no full file scan. Two block categories are deliberately NOT
/// copied: the location index's own B+-tree nodes (offset-addressed derived data,
/// rebuilt from scratch for the new layout by US-EMDB-89-7) and the old Checkpoint
/// chain (a single fresh Checkpoint is written by US-EMDB-89-7). Neither appears in
/// the location index, so the walk excludes them naturally.</para>
///
/// <para><b>Extension seams for the follow-on slices.</b> After <see cref="CopyLiveBlocks"/>
/// this object stays open, exposing everything US-EMDB-89-7 and US-EMDB-89-8 consume:
/// <list type="bullet">
///   <item><see cref="DestBlockManager"/> — append the rebuilt location-index nodes,
///   fresh IndexRoots, and the fresh Checkpoint into the side file.</item>
///   <item><see cref="CopiedBlocks"/> / <see cref="TryGetNewLocation"/> — build the
///   new location index and remap the Checkpoint's named roots to their new offsets.</item>
///   <item><see cref="SourceCheckpoint"/> — continue <c>CheckpointSequence</c> and
///   read the source roots to remap (spec Section 11.2, Compaction.md Section 5).</item>
///   <item><see cref="FileId"/>, <see cref="ContinuedSuperblockSequence"/>,
///   <see cref="SourcePath"/>, <see cref="SideFilePath"/> — finalize the superblocks
///   and perform the atomic rename + directory fsync (US-EMDB-89-8).</item>
/// </list></para>
///
/// <para>Not thread-safe: a single writer drives one compaction. Dispose releases the
/// side-file and source handles; on failure paths the factory disposes them for you.</para>
/// </summary>
public sealed class Compactor : IDisposable
{
    /// <summary>The suffix appended to a shard's path to name its in-progress side file (spec Section 11.2).</summary>
    public const string SideFileSuffix = ".compact";

    private readonly FileStream _sourceStream;
    private readonly OpenState _source;
    private readonly FileStream _destStream;
    private readonly List<CopiedBlock> _copiedBlocks = new();
    private readonly List<CopiedBlock> _copiedRoots = new();
    private readonly Dictionary<string, CopiedBlock> _byBlockId = new(StringComparer.Ordinal);
    private EpochDekProvider? _reEncryptProvider;
    private byte[]? _keyStoreKek;               // owned copy for DEK pruning, zeroized on dispose
    private CheckpointRootPointer _newKeyStorePointer = CheckpointRootPointer.None;
    private IReadOnlyList<ushort> _prunedEpochs = Array.Empty<ushort>();

    private bool _copied;
    private bool _rebuilt;
    private bool _swapped;
    private bool _handlesClosed;
    private bool _disposed;

    private BlockLocationIndex? _newLocationIndex;
    private LocationIndexRoot? _newLocationIndexRoot;
    private Checkpoint? _newCheckpoint;
    private CheckpointRootPointer? _newCheckpointPointer;

    private Compactor(
        string sourcePath,
        string sideFilePath,
        FileStream sourceStream,
        OpenState source,
        FileStream destStream,
        BlockManager destManager,
        ulong continuedSuperblockSequence)
    {
        SourcePath = sourcePath;
        SideFilePath = sideFilePath;
        _sourceStream = sourceStream;
        _source = source;
        _destStream = destStream;
        DestBlockManager = destManager;
        ContinuedSuperblockSequence = continuedSuperblockSequence;
    }

    /// <summary>Path of the source shard being compacted (<c>&lt;name&gt;.emdb</c>).</summary>
    public string SourcePath { get; }

    /// <summary>Path of the side file this compaction writes (<c>&lt;name&gt;.emdb.compact</c>).</summary>
    public string SideFilePath { get; }

    /// <summary>The file's 16-byte ULID, carried unchanged into the side file's superblocks (spec Section 11.2).</summary>
    public byte[] FileId => (byte[])_source.Superblock.FileId.Clone();

    /// <summary>
    /// The <c>SuperblockSequence</c> of the side file's current (higher-sequence) superblock slot —
    /// the source's sequence continued past both freshly written slots (spec Section 11.2: the sequence
    /// "continues, incremented"). US-EMDB-89-8 continues from here when it finalizes the superblocks.
    /// </summary>
    public ulong ContinuedSuperblockSequence { get; }

    /// <summary>
    /// The side file's block writer, positioned after the copied blocks. US-EMDB-89-7 appends the
    /// rebuilt location-index nodes, IndexRoots, and fresh Checkpoint through it; the caller must NOT
    /// dispose it (this <see cref="Compactor"/> owns it).
    /// </summary>
    public BlockManager DestBlockManager { get; }

    /// <summary>The validated source Checkpoint with every root resolved — the snapshot compaction copies from.</summary>
    public ResolvedCheckpoint SourceCheckpoint => _source.Checkpoint;

    /// <summary>The live BlockLocationIndex of the source snapshot — the authoritative set of blocks to copy.</summary>
    public BlockLocationIndex SourceLocationIndex => _source.LocationIndex;

    /// <summary>The blocks copied by <see cref="CopyLiveBlocks"/>, in ascending BlockId order; empty until then.</summary>
    public IReadOnlyList<CopiedBlock> CopiedBlocks => _copiedBlocks;

    /// <summary>
    /// The BlockLocationIndex rebuilt for the side file's physical layout by
    /// <see cref="RebuildLocationIndexAndWriteCheckpoint"/>; null until it runs. Its
    /// <see cref="BlockLocationIndex.Root"/> maps every copied BlockId to its new
    /// <c>(Offset, Length)</c> in the side file (null when no live blocks were copied).
    /// </summary>
    public BlockLocationIndex? NewLocationIndex => _newLocationIndex;

    /// <summary>
    /// The fresh offset-addressed location-root descriptor
    /// <see cref="RebuildLocationIndexAndWriteCheckpoint"/> emitted for the rebuilt
    /// index (Sequence 0 — the compacted file's first version); null until it runs, or
    /// when the compacted file has no live blocks (empty index, no root).
    /// </summary>
    public LocationIndexRoot? NewLocationIndexRoot => _newLocationIndexRoot;

    /// <summary>
    /// The fresh Checkpoint <see cref="RebuildLocationIndexAndWriteCheckpoint"/> wrote
    /// into the side file — the compacted file's single commit point; null until it
    /// runs. Its <see cref="Checkpoint.CheckpointSequence"/> continues the source's
    /// (spec Section 11.2, Compaction.md Section 5) and its
    /// <see cref="Checkpoint.PreviousCheckpoint"/> is <see cref="CheckpointRootPointer.None"/>
    /// (the old chain is not carried into the new file).
    /// </summary>
    public Checkpoint? NewCheckpoint => _newCheckpoint;

    /// <summary>
    /// The ULID+offset pointer to the fresh Checkpoint block in the side file — the
    /// hint US-EMDB-89-8 stamps into the side file's superblocks (with
    /// <c>CleanShutdown = 1</c>) when it finalizes the atomic swap; null until
    /// <see cref="RebuildLocationIndexAndWriteCheckpoint"/> runs.
    /// </summary>
    public CheckpointRootPointer? NewCheckpointPointer => _newCheckpointPointer;

    /// <summary>
    /// The epochs whose DEKs <see cref="RebuildLocationIndexAndWriteCheckpoint"/> pruned from
    /// the side file's KeyStore (docs/Compaction.md Section 4) — the non-active epochs that had
    /// zero remaining block references after the re-encrypting copy, in ascending order. Empty
    /// until the rebuild runs, and always empty unless <see cref="CopyLiveBlocks"/> ran with a
    /// re-encrypting provider AND a KeyStore KEK (a verbatim copy prunes nothing).
    /// </summary>
    public IReadOnlyList<ushort> PrunedEpochs => _prunedEpochs;

    /// <summary>
    /// Opens a clean snapshot of <paramref name="sourcePath"/> and prepares its
    /// <c>&lt;name&gt;.emdb.compact</c> side file with fresh dual-slot superblocks
    /// (same <see cref="FileId"/>, <c>SuperblockSequence</c> continued from the
    /// source), ready for <see cref="CopyLiveBlocks"/>.
    ///
    /// <para>Compaction operates on a committed, cleanly-openable snapshot: a file
    /// that needs dirty-open recovery or encryption bootstrap must be brought up
    /// first, so those outcomes fail here with an explanatory message rather than
    /// compacting an inconsistent view. A stale leftover side file is overwritten
    /// (it was never renamed, so it was never current — spec Section 11.2).</para>
    /// </summary>
    /// <param name="sourcePath">Path of the shard to compact.</param>
    /// <param name="encryptionBootstrap">Encryption bootstrap for an encrypted source (spec Section 10.2 step 2); omit for plaintext.</param>
    public static Result<Compactor> Begin(string sourcePath, IEncryptionBootstrap? encryptionBootstrap = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var sideFilePath = sourcePath + SideFileSuffix;

        FileStream sourceStream;
        try
        {
            sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<Compactor>.Failure($"Compaction could not open source file '{sourcePath}': {ex.Message}");
        }

        var opened = CleanOpener.Open(sourceStream, encryptionBootstrap);
        if (opened.IsFailure)
        {
            sourceStream.Dispose();
            return Result<Compactor>.Failure($"Compaction could not open a clean snapshot of '{sourcePath}': {opened.Error}");
        }
        if (opened.Value.Kind != OpenOutcomeKind.CleanOpen || opened.Value.State is null)
        {
            sourceStream.Dispose();
            return Result<Compactor>.Failure(
                $"Compaction requires a cleanly-openable snapshot of '{sourcePath}', but the open resolved to " +
                $"{opened.Value.Kind} ({opened.Value.Detail}); run recovery/bootstrap before compacting.");
        }

        var source = opened.Value.State;

        FileStream destStream;
        try
        {
            destStream = new FileStream(sideFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            source.Dispose();
            sourceStream.Dispose();
            return Result<Compactor>.Failure($"Compaction could not create side file '{sideFilePath}': {ex.Message}");
        }

        // Fresh dual-slot superblock: clone the source's (same FileId, encryption params,
        // MaxPayloadLength, shard identity), continue the sequence, and mark the file
        // in-progress — no committed Checkpoint yet (US-EMDB-89-7 writes it), and
        // CleanShutdown = 0 so a leftover side file found on open is treated as stale.
        ulong continuedSequence;
        var written = WriteFreshSuperblocks(destStream, source.Superblock, out continuedSequence);
        if (written.IsFailure)
        {
            destStream.Dispose();
            TryDelete(sideFilePath);
            source.Dispose();
            sourceStream.Dispose();
            return Result<Compactor>.Failure($"Compaction could not write the side file's superblocks: {written.Error}");
        }

        var destManager = new BlockManager(
            destStream,
            maxPayloadLength: source.Superblock.MaxPayloadLength,
            offsetMap: new RuntimeBlockOffsetMap(),
            ownsStream: false);

        return Result<Compactor>.Success(new Compactor(
            sourcePath, sideFilePath, sourceStream, source, destStream, destManager, continuedSequence));
    }

    /// <summary>
    /// Walks the source's live BlockLocationIndex and copies every live block into the
    /// side file, preserving each BlockId, then fsyncs the copy. Records each block's
    /// old→new offset in <see cref="CopiedBlocks"/>. Idempotent-guarded: it runs once
    /// per compaction.
    ///
    /// <para>By default (<paramref name="reEncrypt"/> false) blocks are copied verbatim
    /// (spec Section 11.2). With <paramref name="reEncrypt"/> true every DEK-encrypted
    /// block is decrypted with its original-epoch DEK and re-encrypted under
    /// <paramref name="reEncryptProvider"/>'s active epoch with a fresh random nonce and
    /// AAD recomputed for the new epoch — the same BlockId, so pointers are unchanged
    /// (docs/Compaction.md Section 4). The KEK-sealed KeyStore and any plaintext block
    /// are always copied verbatim; the copied KeyStore retains every epoch (pruning is
    /// US-EMDB-90-6). The provider must hold a live DEK for every epoch the live blocks
    /// reference — i.e. the source file's own provider before any pruning.</para>
    ///
    /// <para>Fails (leaving the side file to be discarded) if a live block cannot be
    /// read, if a block does not carry the BlockId the index maps it to (a corrupt index
    /// or misdirected write), if a copied block changes size, or — when re-encrypting —
    /// if a block cannot be decrypted at its stored epoch or re-encrypted at the active
    /// epoch.</para>
    /// </summary>
    /// <param name="reEncrypt">
    /// When true, re-encrypt every DEK-encrypted block at the active epoch with a fresh
    /// nonce (requires <paramref name="reEncryptProvider"/>); when false (default), copy
    /// every block's ciphertext verbatim.
    /// </param>
    /// <param name="reEncryptProvider">
    /// The source file's DEK provider (all epochs live), used to decrypt at the stored
    /// epoch and re-encrypt at the active epoch. Required when <paramref name="reEncrypt"/>
    /// is true; ignored otherwise.
    /// </param>
    /// <param name="keyStoreKek">
    /// The 32-byte KEK that seals the file's KeyStore block. Supplied only alongside
    /// <paramref name="reEncrypt"/> to enable DEK pruning (US-EMDB-90-6): after the copy the
    /// rebuild re-seals the KeyStore with this KEK, retiring every non-active epoch that has
    /// zero remaining block references (docs/Compaction.md Section 4). Omit it (default) to copy
    /// the KeyStore verbatim and prune nothing — the required behavior when
    /// <paramref name="reEncrypt"/> is false, and the safe fallback when a KEK is unavailable.
    /// </param>
    /// <returns>The number of blocks copied.</returns>
    public Result<int> CopyLiveBlocks(
        bool reEncrypt = false,
        EpochDekProvider? reEncryptProvider = null,
        ReadOnlyMemory<byte> keyStoreKek = default)
    {
        ThrowIfDisposed();
        if (_copied)
            return Result<int>.Failure("CopyLiveBlocks has already run for this compaction.");
        if (reEncrypt && reEncryptProvider is null)
            return Result<int>.Failure(
                "CopyLiveBlocks(reEncrypt: true) requires an EpochDekProvider to decrypt the source epochs and re-encrypt at the active epoch.");
        if (!keyStoreKek.IsEmpty && keyStoreKek.Length != AesGcmBlockCipher.KeySize)
            return Result<int>.Failure(
                $"The KeyStore KEK must be exactly {AesGcmBlockCipher.KeySize} bytes to re-seal the pruned KeyStore, got {keyStoreKek.Length}.");

        // Remember the copy mode so the rebuild copies the directly-addressed Checkpoint roots
        // (FolderTree/Metadata/KeyStore, which are not location-index entries) the same way —
        // verbatim, or re-encrypted at the active epoch — as the live blocks copied here.
        _reEncryptProvider = reEncrypt ? reEncryptProvider : null;

        // DEK pruning re-seals the KeyStore at the active epoch, so it only applies to a
        // re-encrypting copy and only when the caller supplied the KEK (a verbatim copy — and any
        // copy without the KEK — leaves the KeyStore, and every epoch's DEK, untouched).
        _keyStoreKek = reEncrypt && !keyStoreKek.IsEmpty ? keyStoreKek.ToArray() : null;

        var live = WalkLiveBlocks(_source.LocationIndex);
        if (live.IsFailure)
            return Result<int>.Failure(live.Error);

        var copied = CopyBlocks(
            _source.BlockManager, DestBlockManager, live.Value,
            reEncrypt ? reEncryptProvider : null);
        if (copied.IsFailure)
            return Result<int>.Failure(copied.Error);

        // Make the copied region durable. The full swap-time fsync ordering (fsync
        // file -> rename -> fsync directory) is US-EMDB-89-8; this fsync bounds the
        // copy so the follow-on slices append onto durable bytes.
        var flush = DestBlockManager.Flush();
        if (flush.IsFailure)
            return Result<int>.Failure($"Compaction could not fsync the copied blocks: {flush.Error}");

        _copiedBlocks.AddRange(copied.Value);
        foreach (var block in copied.Value)
            _byBlockId[Convert.ToHexString(block.BlockId)] = block;
        _copied = true;

        return Result<int>.Success(_copiedBlocks.Count);
    }

    /// <summary>
    /// Looks up the side-file offset/length a copied block now occupies, by its
    /// (unchanged) BlockId. US-EMDB-89-7 uses this to remap the fresh Checkpoint's
    /// named roots — folder tree, primary index, metadata, KeyStore, secondary
    /// indexes — from their source offsets to their new positions.
    /// </summary>
    /// <param name="blockId">The block's 16-byte ULID.</param>
    /// <param name="newOffset">The block's offset in the side file, when copied.</param>
    /// <param name="newLength">The block's total on-disk length, when copied.</param>
    /// <returns>True when the block was copied; false when it is not part of the live set.</returns>
    public bool TryGetNewLocation(ReadOnlySpan<byte> blockId, out long newOffset, out long newLength)
    {
        if (_byBlockId.TryGetValue(Convert.ToHexString(blockId), out var block))
        {
            newOffset = block.NewOffset;
            newLength = block.NewLength;
            return true;
        }
        newOffset = 0;
        newLength = 0;
        return false;
    }

    /// <summary>
    /// Rebuilds the BlockLocationIndex for the side file's new physical layout and
    /// writes the compacted file's fresh Checkpoint (spec Section 11.2 steps 3-4,
    /// docs/Compaction.md Section 2). Runs after <see cref="CopyLiveBlocks"/>: the
    /// copied blocks kept their BlockIds but moved, so the derived location index —
    /// the only structure holding raw offsets — is discarded and regenerated bottom-up
    /// from the copy's old→new offset map.
    ///
    /// <para><b>Bulk-load in write order.</b> One <c>BlockId → (NewOffset, NewLength)</c>
    /// entry per copied block is batch-inserted into a fresh
    /// <see cref="BlockLocationIndex"/> over the side file in a single copy-on-write
    /// pass (<see cref="BlockLocationIndex.PutBatch"/> — the same one-pass bulk load the
    /// checkpoint delta uses), so the rebuild rides along in one sequential pass
    /// (Compaction.md Section 6). The index's own B+-tree nodes append after the copied
    /// blocks; being offset-addressed they are deliberately NOT entries of the index
    /// (spec Section 7). The new nodes are fsynced before the Checkpoint that commits
    /// them (spec Section 10.3 write order).</para>
    ///
    /// <para><b>Fresh IndexRoots + Checkpoint.</b> Every named root the source
    /// Checkpoint carried — folder tree, primary index, metadata, KeyStore, and each
    /// secondary index — is a live ULID-addressed block that was copied, so its pointer
    /// is remapped to the new offset by <see cref="TryGetNewLocation"/> (the BlockId is
    /// unchanged, spec Section 11.2). The location root points at the rebuilt tree's new
    /// root node. The Checkpoint continues the source's
    /// <see cref="Checkpoint.CheckpointSequence"/> so restore points stay ordered
    /// (Compaction.md Section 5), links <see cref="CheckpointRootPointer.None"/> as its
    /// previous (the compacted file starts a fresh chain), and records
    /// <c>DeadByteCount = 0</c> — a freshly compacted file has reclaimed all dead space.
    /// The atomic rename and the superblock finalize that make this Checkpoint the
    /// file's committed hint are US-EMDB-89-8; this slice leaves the durable side file
    /// with its fresh Checkpoint appended and exposes it via
    /// <see cref="NewCheckpoint"/>/<see cref="NewCheckpointPointer"/>.</para>
    /// </summary>
    /// <param name="maxLeafEntries">Test hook to force B+-tree splits cheaply in the rebuilt index; null uses the spec capacity.</param>
    /// <param name="maxInternalKeys">Test hook to force B+-tree splits cheaply in the rebuilt index; null uses the spec capacity.</param>
    public Result<Checkpoint> RebuildLocationIndexAndWriteCheckpoint(
        int? maxLeafEntries = null, int? maxInternalKeys = null)
    {
        ThrowIfDisposed();
        if (!_copied)
            return Result<Checkpoint>.Failure(
                "RebuildLocationIndexAndWriteCheckpoint requires CopyLiveBlocks to have run first.");
        if (_rebuilt)
            return Result<Checkpoint>.Failure(
                "RebuildLocationIndexAndWriteCheckpoint has already run for this compaction.");

        // 0. Copy any directly-addressed Checkpoint root that CopyLiveBlocks (the BlockLocationIndex
        //    walk) did not carry into the side file. EmailManager addresses its FolderTree, Metadata,
        //    and KeyStore roots straight from the Checkpoint — they are NOT location-index entries
        //    ("the initial structural blocks are addressed directly by these root pointers",
        //    EmailManager.Create) — so the live-index walk misses them and the fresh Checkpoint could
        //    not remap them, dangling every root of a real mailbox. This carries every root the
        //    Checkpoint names into the side file so RemapRoot resolves it (spec Section 11.2).
        var rootsCopied = CopyDirectlyAddressedRoots();
        if (rootsCopied.IsFailure)
            return Result<Checkpoint>.Failure(rootsCopied.Error);

        // 1. Bulk-load a fresh location index over the side file from the copy's
        //    old→new offset map: one entry per copied block, in write order. The
        //    offset-addressed nodes need no BlockId resolver (spec Section 7).
        var nodeStore = new BTreeNodeStore(DestBlockManager, blockIdResolver: null);
        var index = new BlockLocationIndex(nodeStore, maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

        var newLocations = new List<BlockLocation>(_copiedBlocks.Count);
        foreach (var block in _copiedBlocks)
            newLocations.Add(new BlockLocation
            {
                BlockId = block.BlockId,
                Offset = block.NewOffset,
                TotalBlockLength = block.NewLength,
            });

        var loaded = index.PutBatch(newLocations);
        if (loaded.IsFailure)
            return Result<Checkpoint>.Failure(
                $"Compaction could not bulk-load the rebuilt BlockLocationIndex: {loaded.Error}");

        // 2. fsync the rebuilt index nodes before the Checkpoint that references them
        //    (spec Section 10.3 write order: nodes → fsync → root pointer → Checkpoint).
        var nodesFsync = DestBlockManager.Flush();
        if (nodesFsync.IsFailure)
            return Result<Checkpoint>.Failure(
                $"Compaction could not fsync the rebuilt index nodes: {nodesFsync.Error}");

        // 3. Locate the rebuilt tree's new root node and describe it. An empty index
        //    (a file with no live blocks) names no location root.
        LocationIndexRoot? locationRoot = null;
        var locationPointer = CheckpointRootPointer.None;
        if (index.Root is not null)
        {
            long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root.RootRef.Reference);
            var rootBlock = DestBlockManager.Read(rootOffset);
            if (rootBlock.IsFailure)
                return Result<Checkpoint>.Failure(
                    $"Compaction could not read the rebuilt location-index root node at side-file offset {rootOffset}: {rootBlock.Error}");

            locationRoot = LocationIndexRoot.CreateInitial(
                rootOffset, index.Root.EntryCount, index.Root.Height, index.Root.RootHash);
            locationPointer = CheckpointRootPointer.Create(
                (byte[])rootBlock.Value.Header.BlockId.Clone(), rootOffset);
        }

        // 4. Remap every named root the source Checkpoint carried to its new offset
        //    (same BlockId, moved block). A present root that was not copied would be a
        //    dangling pointer in the compacted file — a genuine error, surfaced here.
        var source = _source.Checkpoint;
        var folder = RemapRoot(source.FolderTreeRoot, "FolderTreeRoot");
        if (folder.IsFailure) return Result<Checkpoint>.Failure(folder.Error);
        var primary = RemapRoot(source.PrimaryIndexRoot, "PrimaryIndexRoot");
        if (primary.IsFailure) return Result<Checkpoint>.Failure(primary.Error);
        var metadata = RemapRoot(source.MetadataRoot, "MetadataRoot");
        if (metadata.IsFailure) return Result<Checkpoint>.Failure(metadata.Error);
        var keyStore = RemapRoot(source.KeyStoreRoot, "KeyStoreRoot");
        if (keyStore.IsFailure) return Result<Checkpoint>.Failure(keyStore.Error);
        // The KeyStore block moved (and, when pruned, was re-sealed under the same BlockId at a new
        // offset). FinalizeAndSwap stamps this remapped pointer onto the side file's superblock so its
        // ActiveKeyStore pointer resolves in the compacted layout (the open-time bootstrap reads the
        // KeyStore through the superblock, spec Section 10.2 step 2), not the stale source offset.
        _newKeyStorePointer = keyStore.Value;

        var secondaries = new CheckpointSecondaryIndex[source.SecondaryIndexes.Count];
        for (int i = 0; i < secondaries.Length; i++)
        {
            var entry = source.SecondaryIndexes[i];
            var remapped = RemapRoot(entry.Root, $"SecondaryIndexes[{i}] ({entry.IndexKind})");
            if (remapped.IsFailure) return Result<Checkpoint>.Failure(remapped.Error);
            secondaries[i] = new CheckpointSecondaryIndex
            {
                IndexKind = entry.IndexKind,
                Pointer = remapped.Value,
            };
        }

        // 5. Continue the source's CheckpointSequence so restore points stay ordered
        //    (Compaction.md Section 5); the compacted file starts a fresh chain.
        var sourceCheckpoint = source.Checkpoint;
        if (sourceCheckpoint.CheckpointSequence == ulong.MaxValue)
            return Result<Checkpoint>.Failure(
                "Compaction cannot continue the CheckpointSequence: the source is already at ulong.MaxValue.");

        var checkpoint = new Checkpoint
        {
            FormatVersion = BlockSerializer.CurrentFormatVersion,
            CheckpointSequence = sourceCheckpoint.CheckpointSequence + 1,
            FileId = FileId,
            FolderTreeRoot = folder.Value,
            PrimaryIndexRoot = primary.Value,
            LocationIndexRoot = locationPointer,
            MetadataRoot = metadata.Value,
            KeyStoreRoot = keyStore.Value,
            PreviousCheckpoint = CheckpointRootPointer.None,
            SecondaryIndexes = secondaries,
            // Exactly the copied live blocks; their verbatim payload bytes preserve the
            // live byte count, and a freshly compacted file has zero reclaimable dead space.
            LiveBlockCount = _copiedBlocks.Count,
            LiveByteCount = sourceCheckpoint.LiveByteCount,
            DeadByteCount = 0,
        };

        byte[] payload;
        try
        {
            payload = CheckpointSerializer.Serialize(checkpoint);
        }
        catch (ArgumentException ex)
        {
            return Result<Checkpoint>.Failure(
                $"Compaction produced an invalid Checkpoint payload: {ex.Message}");
        }

        // 6. Append the Checkpoint block, then fsync so the commit point is durable
        //    (spec Section 10.3). US-EMDB-89-8 renames the side file and points the
        //    superblock at this Checkpoint.
        var appended = DestBlockManager.Append(BlockType.Checkpoint, PayloadEncoding.Custom, payload);
        if (appended.IsFailure)
            return Result<Checkpoint>.Failure(
                $"Compaction could not append the fresh Checkpoint block: {appended.Error}");

        var checkpointFsync = DestBlockManager.Flush();
        if (checkpointFsync.IsFailure)
            return Result<Checkpoint>.Failure(
                $"Compaction could not fsync the fresh Checkpoint block: {checkpointFsync.Error}");

        _newLocationIndex = index;
        _newLocationIndexRoot = locationRoot;
        _newCheckpoint = checkpoint;
        _newCheckpointPointer = CheckpointRootPointer.Create(
            (byte[])appended.Value.BlockId.Clone(), appended.Value.Offset);
        _rebuilt = true;

        return Result<Checkpoint>.Success(checkpoint);
    }

    /// <summary>
    /// Copies into the side file every root the source Checkpoint names that
    /// <see cref="CopyLiveBlocks"/> did not already copy — i.e. the directly-addressed structural
    /// roots (FolderTree, Metadata, KeyStore) that EmailManager addresses straight from the
    /// Checkpoint rather than through the BlockLocationIndex (EmailManager.Create). The location-
    /// index-resident roots (primary index, date index, whose IndexRoot blocks are folded into the
    /// index on commit) are already among <see cref="CopiedBlocks"/>, so they are skipped; the
    /// LocationIndex root is excluded entirely (the rebuild regenerates it from scratch).
    ///
    /// <para>Each copied root is recorded in the old→new offset map (so <see cref="RemapRoot"/>
    /// resolves it) but is deliberately NOT added to <see cref="CopiedBlocks"/> or the rebuilt
    /// location index — the compacted file keeps the source's directly-addressed layout, so
    /// <see cref="Checkpoint.LiveBlockCount"/> still equals the location-index entry count. Roots are
    /// copied verbatim, or re-encrypted at the active epoch when <see cref="CopyLiveBlocks"/> ran with
    /// a provider (an encrypted FolderTree must move epochs with the rest, docs/Compaction.md
    /// Section 4). Fails if a named root's block cannot be read/copied (a missing root would leave the
    /// compacted Checkpoint with a dangling pointer).</para>
    /// </summary>
    private Result CopyDirectlyAddressedRoots()
    {
        var source = _source.Checkpoint;
        var roots = new List<BlockLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Consider(ResolvedRoot? root)
        {
            if (root is null)
                return;
            string key = Convert.ToHexString(root.BlockId);
            // Already carried into the side file as a live block, or already gathered this pass.
            if (_byBlockId.ContainsKey(key) || !seen.Add(key))
                return;
            roots.Add(new BlockLocation
            {
                BlockId = root.BlockId,
                Offset = root.Offset,
                TotalBlockLength = root.TotalBlockLength,
            });
        }

        Consider(source.FolderTreeRoot);
        Consider(source.PrimaryIndexRoot);
        Consider(source.MetadataRoot);
        Consider(source.KeyStoreRoot);
        foreach (var secondary in source.SecondaryIndexes)
            Consider(secondary.Root);
        // source.LocationIndexRoot is intentionally NOT considered: it is rebuilt, not copied.

        // When DEK pruning is active, the KeyStore root is not copied verbatim: it is re-sealed
        // under the KEK with unreferenced epochs retired (below). Pull it out of the verbatim set so
        // it is not copied twice; the source root (with its offset) is what the re-seal reads.
        BlockLocation? keyStoreToPrune = null;
        if (_keyStoreKek is not null && source.KeyStoreRoot is not null)
        {
            string keyStoreKey = Convert.ToHexString(source.KeyStoreRoot.BlockId);
            int idx = roots.FindIndex(r => Convert.ToHexString(r.BlockId) == keyStoreKey);
            if (idx >= 0)
            {
                keyStoreToPrune = roots[idx];
                roots.RemoveAt(idx);
            }
        }

        if (roots.Count > 0)
        {
            var copied = CopyBlocks(_source.BlockManager, DestBlockManager, roots, _reEncryptProvider);
            if (copied.IsFailure)
                return Result.Failure(
                    $"Compaction could not copy the directly-addressed Checkpoint roots: {copied.Error}");

            foreach (var block in copied.Value)
            {
                _copiedRoots.Add(block);
                _byBlockId[Convert.ToHexString(block.BlockId)] = block;
            }
        }

        // Now that every other block is in the side file, re-seal the KeyStore with the epochs that
        // no copied block still references retired (docs/Compaction.md Section 4). Deferred to here so
        // the reference count sees the fully re-encrypted layout (every block now sits at the active
        // epoch), not the source epochs.
        if (keyStoreToPrune is { } ks)
        {
            var pruned = PruneAndWriteKeyStore(ks);
            if (pruned.IsFailure)
                return Result.Failure(pruned.Error);
        }

        return Result.Success();
    }

    /// <summary>
    /// Re-seals the file's KeyStore into the side file with every non-active epoch that has zero
    /// remaining block references retired — the DEK-pruning step of a re-encrypting compaction
    /// (docs/Compaction.md Section 4, US-EMDB-90-6). It (1) counts, over every block already written
    /// to the side file, the epochs still referenced by a DEK-encrypted block header (the KeyStore's
    /// own KEK epoch does not count — it is KEK-sealed, not DEK-encrypted); (2) decrypts the source
    /// KeyStore with the KEK, retires each live non-active epoch with a zero count (dropping its 32
    /// DEK bytes) while leaving the active epoch and any already-retired entry alone; and (3) re-seals
    /// the pruned table under the same KEK with a fresh nonce and appends it under the SAME BlockId so
    /// the Checkpoint's remapped KeyStoreRoot and the superblock pointer resolve to it unchanged.
    ///
    /// <para>The source file is never touched: pruning writes only into the side file, so a failed or
    /// abandoned compaction leaves every epoch's DEK intact in the original.</para>
    /// </summary>
    private Result PruneAndWriteKeyStore(BlockLocation sourceKeyStore)
    {
        // 1. Read + KEK-decrypt the source KeyStore table.
        var read = _source.BlockManager.Read(sourceKeyStore.Offset);
        if (read.IsFailure)
            return Result.Failure(
                $"Compaction could not read the KeyStore block at source offset {sourceKeyStore.Offset} to prune it: {read.Error}");
        var block = read.Value;
        if (block.Header.Type != BlockType.KeyStore)
            return Result.Failure(
                $"Block at the Checkpoint's KeyStore root (offset {sourceKeyStore.Offset}) is a {block.Header.Type}, not a KeyStore; refusing to prune.");

        byte[] plaintext;
        try
        {
            plaintext = AesGcmBlockCipher.Decrypt(
                block.Payload, _keyStoreKek, _source.Superblock.FileId,
                block.Header.BlockId, BlockType.KeyStore, block.Header.KeyEpoch);
        }
        catch (WrongKeyOrTamperError ex)
        {
            return Result.Failure(
                $"Compaction could not decrypt the KeyStore to prune it (wrong KEK or tampering): {ex.Message}.");
        }
        catch (ArgumentException ex)
        {
            return Result.Failure($"Compaction found a malformed KeyStore block while pruning: {ex.Message}.");
        }

        KeyStoreBlock? table = null;
        try
        {
            var parsed = KeyStoreSerializer.Deserialize(plaintext);
            if (parsed.IsFailure)
                return Result.Failure($"Compaction could not parse the KeyStore table to prune it: {parsed.Error}");
            table = parsed.Value;

            // 2. Count the epochs still referenced by a DEK-encrypted block in the side file, then
            //    retire every live non-active epoch with a zero count.
            var references = CountEpochReferencesInSideFile();
            if (references.IsFailure)
                return Result.Failure(references.Error);

            var pruned = new List<ushort>();
            foreach (var entry in table.Entries)
            {
                if (entry.Epoch == table.ActiveEpoch || entry.Retired)
                    continue;
                if (references.Value.Contains(entry.Epoch))
                    continue;
                // Zero remaining references: drop the DEK bytes and tombstone the epoch.
                CryptographicOperations.ZeroMemory(entry.Dek);
                entry.Dek = Array.Empty<byte>();
                entry.Retired = true;
                pruned.Add(entry.Epoch);
            }
            pruned.Sort();
            _prunedEpochs = pruned;

            // 3. Re-seal the pruned table under the same KEK, same BlockId, fresh nonce.
            var written = WritePrunedKeyStore(table, block.Header.BlockId);
            if (written.IsFailure)
                return Result.Failure(written.Error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            // The parsed table's live DEK bytes are transient copies of the source key material; scrub them.
            if (table is not null)
                foreach (var entry in table.Entries)
                    CryptographicOperations.ZeroMemory(entry.Dek);
        }

        return Result.Success();
    }

    /// <summary>
    /// Counts, over every block already written to the side file (the copied live blocks plus the
    /// directly-addressed roots gathered so far), the set of DEK epochs still referenced by an
    /// encrypted block header. The KeyStore block is excluded — it is KEK-sealed, not DEK-encrypted,
    /// so its <c>KeyEpoch = 0</c> is not a reference to DEK epoch 0 — as is every plaintext block.
    /// After a re-encrypting copy this is just the active epoch, so every other epoch prunes.
    /// </summary>
    private Result<HashSet<ushort>> CountEpochReferencesInSideFile()
    {
        var referenced = new HashSet<ushort>();

        Result ScanNewOffset(long newOffset)
        {
            var read = DestBlockManager.Read(newOffset);
            if (read.IsFailure)
                return Result.Failure(
                    $"Compaction could not re-read a side-file block at offset {newOffset} to count epoch references: {read.Error}");
            var header = read.Value.Header;
            if (header.IsEncrypted && header.Type != BlockType.KeyStore)
                referenced.Add(header.KeyEpoch);
            return Result.Success();
        }

        foreach (var b in _copiedBlocks)
        {
            var scan = ScanNewOffset(b.NewOffset);
            if (scan.IsFailure) return Result<HashSet<ushort>>.Failure(scan.Error);
        }
        foreach (var b in _copiedRoots)
        {
            var scan = ScanNewOffset(b.NewOffset);
            if (scan.IsFailure) return Result<HashSet<ushort>>.Failure(scan.Error);
        }

        return Result<HashSet<ushort>>.Success(referenced);
    }

    /// <summary>
    /// Serializes and KEK-seals <paramref name="table"/> and appends it to the side file under
    /// <paramref name="blockId"/> — the source KeyStore's own BlockId — so the Checkpoint's remapped
    /// KeyStoreRoot and the superblock pointer resolve to the pruned block without changing identity.
    /// Records the new location in the old→new map (so <see cref="RemapRoot"/> finds it) but not in
    /// <see cref="CopiedBlocks"/> or the rebuilt location index, exactly like the verbatim root copy.
    /// </summary>
    private Result WritePrunedKeyStore(KeyStoreBlock table, byte[] blockId)
    {
        byte[] plaintext;
        try
        {
            plaintext = KeyStoreSerializer.Serialize(table);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure($"Compaction could not serialize the pruned KeyStore table: {ex.Message}.");
        }

        try
        {
            byte[] ciphertext = AesGcmBlockCipher.Encrypt(
                plaintext, _keyStoreKek, _source.Superblock.FileId, blockId, BlockType.KeyStore, keyEpoch: 0);
            var appended = DestBlockManager.Append(
                BlockType.KeyStore, PayloadEncoding.Custom, ciphertext,
                compression: CompressionAlgorithm.None, encrypted: true, keyEpoch: 0, blockId: blockId);
            if (appended.IsFailure)
                return Result.Failure($"Compaction could not append the pruned KeyStore block: {appended.Error}");

            // SourceOffset 0: the pruned KeyStore is freshly sealed, not copied from a source offset.
            var copied = new CopiedBlock(
                (byte[])blockId.Clone(), 0, appended.Value.Offset, appended.Value.TotalBlockLength);
            _copiedRoots.Add(copied);
            _byBlockId[Convert.ToHexString(blockId)] = copied;
            return Result.Success();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Remaps one of the source Checkpoint's named roots to the offset its (verbatim,
    /// same-BlockId) copy now occupies in the side file. An absent source root stays
    /// <see cref="CheckpointRootPointer.None"/>; a present root that was not copied
    /// (so it is not in the live set the compaction moved) is a dangling pointer in the
    /// compacted file and fails the rebuild.
    /// </summary>
    private Result<CheckpointRootPointer> RemapRoot(ResolvedRoot? sourceRoot, string name)
    {
        if (sourceRoot is null)
            return Result<CheckpointRootPointer>.Success(CheckpointRootPointer.None);

        if (!TryGetNewLocation(sourceRoot.BlockId, out long newOffset, out _))
            return Result<CheckpointRootPointer>.Failure(
                $"Compaction cannot remap Checkpoint {name} (block {Convert.ToHexString(sourceRoot.BlockId)}): " +
                "it is not among the copied live blocks, so the compacted file would carry a dangling root " +
                "(a named ULID-addressed root must be present in the source BlockLocationIndex, spec Section 7).");

        return Result<CheckpointRootPointer>.Success(
            CheckpointRootPointer.Create((byte[])sourceRoot.BlockId.Clone(), newOffset));
    }

    /// <summary>
    /// True once <see cref="FinalizeAndSwap"/> has atomically renamed the side file
    /// over the source; the source path now names the compacted file and the side
    /// file no longer exists.
    /// </summary>
    public bool HasSwapped => _swapped;

    /// <summary>
    /// Finalizes the side file and atomically swaps it over the source, completing the
    /// compaction (EmailDB_FileFormat_Spec.md Section 11.2 step 5, docs/Compaction.md
    /// Section 2). Runs after <see cref="RebuildLocationIndexAndWriteCheckpoint"/>. In
    /// order it
    /// <list type="number">
    ///   <item><b>finalizes both superblock slots</b> — the fresh Checkpoint written by
    ///   the rebuild becomes the file's committed hint (<c>LastCheckpoint</c> ULID+offset)
    ///   and <c>CleanShutdown = 1</c>, their sequence continued past the in-progress slots
    ///   so the higher slot wins on open;</item>
    ///   <item><b>fsyncs the whole side file</b> so its copied blocks, rebuilt index,
    ///   Checkpoint, and finalized superblocks are all durable BEFORE the rename that makes
    ///   it the file (spec Section 10.3 write order);</item>
    ///   <item><b>closes the source and side handles</b> so the rename can replace the
    ///   destination on every platform (Windows cannot replace a file with open handles);</item>
    ///   <item><b>atomically renames</b> the side file over the source path — POSIX
    ///   <c>rename(2)</c> on this platform, <c>ReplaceFile</c> semantics
    ///   (<see cref="File.Replace(string, string, string)"/>) on Windows (spec Section 11.2);</item>
    ///   <item><b>fsyncs the containing directory</b> so the renamed directory entry —
    ///   the swap's commit point — survives a crash (spec Section 10.3,
    ///   <see cref="DirectoryFsync"/>).</item>
    /// </list>
    ///
    /// <para><b>Crash safety.</b> The rename is the single atomic commit point: a crash
    /// at any earlier step leaves the complete old file at the source path plus a stale
    /// <c>.compact</c> side file (which the next open discards, see
    /// <see cref="CleanupLeftoverSideFile"/>) — the side file is never current until it is
    /// renamed, regardless of its finalized superblocks. A crash after the rename (before
    /// or after the directory fsync) leaves the complete new file; the directory fsync only
    /// guarantees the rename cannot be lost. So every kill yields either the complete old
    /// file or the complete new file, never a hybrid (the acceptance criterion).</para>
    ///
    /// <para>On success the compaction is finished and its handles are already closed;
    /// <see cref="Dispose"/> becomes a no-op. On a rename/fsync failure the source file is
    /// left intact (the rename either happened atomically or did not) and the failure is
    /// surfaced.</para>
    /// </summary>
    public Result FinalizeAndSwap()
    {
        ThrowIfDisposed();
        if (!_rebuilt)
            return Result.Failure(
                "FinalizeAndSwap requires RebuildLocationIndexAndWriteCheckpoint to have run first.");
        if (_swapped)
            return Result.Failure("FinalizeAndSwap has already run for this compaction.");

        // 1-2. Finalize both superblock slots (fresh Checkpoint hint + CleanShutdown = 1)
        //       and fsync the whole side file so every byte is durable before the rename.
        var finalized = WriteFinalizedSuperblocks(
            _destStream, _source.Superblock, ContinuedSuperblockSequence, _newCheckpointPointer!, _newKeyStorePointer);
        if (finalized.IsFailure)
            return Result.Failure(
                $"Compaction could not finalize the side file's superblocks: {finalized.Error}");

        // 3. Close the source and side handles so the rename can replace the destination
        //    on every platform (Windows refuses to replace a file with open handles).
        CloseHandles();

        // 4. Atomically rename the side file over the source (spec Section 11.2). POSIX
        //    rename(2) replaces the destination atomically; Windows File.Replace gives the
        //    ReplaceFile semantics the spec calls for. Either the whole rename happened or
        //    none of it did — never a partial file at the source path.
        try
        {
            if (OperatingSystem.IsWindows())
                File.Replace(SideFilePath, SourcePath, destinationBackupFileName: null);
            else
                File.Move(SideFilePath, SourcePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(
                $"Compaction could not atomically swap the side file over '{SourcePath}': {ex.Message}");
        }

        // 5. fsync the containing directory so the renamed entry — the commit point of the
        //    swap — is durable (spec Section 10.3). A create/rename/delete that is not
        //    directory-synced can un-happen on a crash.
        var dirSync = DirectoryFsync.SyncContainingDirectory(SourcePath);
        if (dirSync.IsFailure)
            return Result.Failure(
                $"Compaction renamed the side file over '{SourcePath}' but could not fsync its directory: {dirSync.Error}");

        _swapped = true;
        return Result.Success();
    }

    /// <summary>
    /// Deletes a leftover <c>&lt;name&gt;.emdb.compact</c> side file next to
    /// <paramref name="sourcePath"/>, if one exists, and fsyncs the directory so the
    /// deletion is durable (EmailDB_FileFormat_Spec.md Section 11.2). A side file is only
    /// ever current after <see cref="FinalizeAndSwap"/> renames it over the source; any
    /// file still bearing the <c>.compact</c> name is from a compaction that was
    /// interrupted before its atomic rename, so it is stale by definition and safe to
    /// discard. Call this on the v3 open path before adopting the source file.
    /// </summary>
    /// <param name="sourcePath">Path of the shard being opened.</param>
    /// <returns>
    /// Success whether or not a side file was present (a missing side file is the common
    /// case — nothing to clean). A failure only when a present side file could not be
    /// deleted or the directory could not be fsynced.
    /// </returns>
    public static Result CleanupLeftoverSideFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var sideFilePath = sourcePath + SideFileSuffix;
        if (!File.Exists(sideFilePath))
            return Result.Success();

        try
        {
            File.Delete(sideFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(
                $"Could not delete leftover compaction side file '{sideFilePath}': {ex.Message}");
        }

        // fsync the directory so the removed entry stays removed after a crash — a deleted
        // file whose directory was not synced can resurrect (spec Section 10.3).
        var dirSync = DirectoryFsync.SyncContainingDirectory(sideFilePath);
        if (dirSync.IsFailure)
            return Result.Failure(
                $"Deleted leftover compaction side file '{sideFilePath}' but could not fsync its directory: {dirSync.Error}");

        return Result.Success();
    }

    /// <summary>
    /// Writes both superblock slots of the finalized side file: the fresh Checkpoint the
    /// rebuild wrote becomes the committed hint and <c>CleanShutdown = 1</c>, continuing
    /// the sequence past the in-progress slots (slot A = <paramref name="continuedSequence"/>
    /// + 1, slot B = + 2, so the higher slot B wins on open). The slots otherwise clone the
    /// source superblock (same FileId, encryption parameters, MaxPayloadLength, shard
    /// identity). Flushes to disk (fsync) before returning so the finalized file is durable
    /// before the caller's rename.
    /// </summary>
    private static Result WriteFinalizedSuperblocks(
        FileStream dest, Superblock source, ulong continuedSequence,
        CheckpointRootPointer checkpoint, CheckpointRootPointer keyStore)
    {
        var final = source.Clone();
        final.CleanShutdown = 1;
        final.LastCheckpointBlockId = (byte[])checkpoint.BlockId.Clone();
        final.LastCheckpointOffset = checkpoint.Offset;

        // Repoint the KeyStore. The block moved in the compacted layout (and, when pruned, was
        // re-sealed under the same BlockId at a new offset), so the cloned source pointer is stale.
        // The open-time bootstrap reads the KeyStore through this superblock pointer (spec Section
        // 10.2 step 2), so an encrypted file would otherwise fail to reopen. Plaintext files carry no
        // KeyStore (EncryptionEnabled = 0) and keep the cloned empty pointer.
        if (source.EncryptionEnabled != 0)
        {
            final.ActiveKeyStoreBlockId = (byte[])keyStore.BlockId.Clone();
            final.ActiveKeyStoreOffset = keyStore.Offset;
        }

        try
        {
            Span<byte> slot = stackalloc byte[SuperblockSerializer.SlotSize];

            final.SuperblockSequence = continuedSequence + 1;
            SuperblockSerializer.Serialize(final, slot);
            dest.Seek(SuperblockManager.SlotAOffset, SeekOrigin.Begin);
            dest.Write(slot);

            final.SuperblockSequence = continuedSequence + 2;
            SuperblockSerializer.Serialize(final, slot);
            dest.Seek(SuperblockManager.SlotBOffset, SeekOrigin.Begin);
            dest.Write(slot);

            dest.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return Result.Failure(ex.Message);
        }

        return Result.Success();
    }

    /// <summary>
    /// Enumerates the live blocks of <paramref name="liveIndex"/> in ascending
    /// BlockId order (spec Section 7: the index holds exactly one entry per live
    /// block). Each entry's 16-byte value is decoded to <c>(Offset, Length)</c> —
    /// the block's location in the source file. Fails only on an I/O or Merkle
    /// verification error while scanning the index (spec Section 13). An empty
    /// index (a file with no live blocks) yields an empty list.
    /// </summary>
    /// <param name="liveIndex">The source snapshot's live BlockLocationIndex.</param>
    public static Result<IReadOnlyList<BlockLocation>> WalkLiveBlocks(BlockLocationIndex liveIndex)
    {
        ArgumentNullException.ThrowIfNull(liveIndex);

        var live = new List<BlockLocation>();
        var scan = liveIndex.Tree.Scan(liveIndex.Root);
        while (true)
        {
            var next = scan.MoveNext();
            if (next.IsFailure)
                return Result<IReadOnlyList<BlockLocation>>.Failure(
                    $"Walking the live BlockLocationIndex failed after {live.Count} entries: {next.Error}");
            if (!next.Value)
                break;

            var entry = scan.Current;
            if (entry.Value.Length != BlockLocationIndex.LocationValueSize)
                return Result<IReadOnlyList<BlockLocation>>.Failure(
                    $"BlockLocationIndex entry {Convert.ToHexString(entry.Key)} has a {entry.Value.Length}-byte value; " +
                    $"expected {BlockLocationIndex.LocationValueSize} (Offset ‖ Length).");

            long offset = BinaryPrimitives.ReadInt64LittleEndian(entry.Value.AsSpan(0, BlockLocationIndex.OffsetSize));
            long length = BinaryPrimitives.ReadInt64LittleEndian(entry.Value.AsSpan(BlockLocationIndex.OffsetSize, BlockLocationIndex.LengthSize));
            live.Add(new BlockLocation
            {
                BlockId = entry.Key,
                Offset = offset,
                TotalBlockLength = length,
            });
        }

        return Result<IReadOnlyList<BlockLocation>>.Success(live);
    }

    /// <summary>
    /// Copies each block in <paramref name="liveBlocks"/> from
    /// <paramref name="source"/> to <paramref name="dest"/>, preserving each block's
    /// BlockId, type, encoding, and compression at its new offset. Every block is read
    /// and fully verified (header/payload checksums) and cross-checked against the
    /// BlockId the index mapped to that offset before it is written. The returned list
    /// pairs each BlockId with its source and new offsets, in input order.
    ///
    /// <para><b>Verbatim (default, <paramref name="reEncryptProvider"/> null).</b> The
    /// on-disk payload bytes — ciphertext when the block is encrypted — are appended
    /// unchanged with the same encryption flag and <c>KeyEpoch</c>, reproducing the
    /// block bit-for-bit (spec Section 11.2: "No logical block content is rewritten …
    /// blocks just move"). No key is needed even for encrypted blocks.</para>
    ///
    /// <para><b>Re-encrypting (<paramref name="reEncryptProvider"/> supplied,
    /// docs/Compaction.md Section 4).</b> Each DEK-encrypted block is decrypted with its
    /// original-epoch DEK, then re-encrypted under the provider's active epoch with a
    /// fresh random nonce and AAD recomputed for the new epoch — with the SAME BlockId,
    /// type, and compression, so only the ciphertext, GCM tag, and the header
    /// <c>KeyEpoch</c> change (the payload length is unchanged: AES-GCM adds a constant
    /// nonce+tag overhead, so the decrypt/re-encrypt round-trip is length-preserving).
    /// Blocks that are not encrypted are copied verbatim, and the KEK-sealed
    /// <see cref="BlockType.KeyStore"/> block is copied verbatim too — it is not
    /// DEK-encrypted, so the DEK provider must never touch it (its key material is
    /// retained until pruning, US-EMDB-90-6). The provider must hold a live DEK for
    /// every epoch the live blocks reference (the source's full table before pruning);
    /// a missing/retired epoch or an authentication failure fails the copy.</para>
    /// </summary>
    /// <param name="source">Block reader over the original file.</param>
    /// <param name="dest">Block writer over the side file.</param>
    /// <param name="liveBlocks">The live blocks to copy (from <see cref="WalkLiveBlocks"/>).</param>
    /// <param name="reEncryptProvider">
    /// When supplied, re-encrypts every DEK-encrypted block at the provider's active epoch
    /// with a fresh nonce; when null, copies every block's ciphertext verbatim.
    /// </param>
    public static Result<IReadOnlyList<CopiedBlock>> CopyBlocks(
        BlockManager source, BlockManager dest, IReadOnlyList<BlockLocation> liveBlocks,
        EpochDekProvider? reEncryptProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dest);
        ArgumentNullException.ThrowIfNull(liveBlocks);

        var copied = new List<CopiedBlock>(liveBlocks.Count);
        foreach (var location in liveBlocks)
        {
            var read = source.Read(location.Offset);
            if (read.IsFailure)
                return Result<IReadOnlyList<CopiedBlock>>.Failure(
                    $"Compaction could not read live block {Convert.ToHexString(location.BlockId)} at source offset " +
                    $"{location.Offset}: {read.Error}");

            var block = read.Value;
            if (!block.Header.BlockId.AsSpan().SequenceEqual(location.BlockId))
                return Result<IReadOnlyList<CopiedBlock>>.Failure(
                    $"BlockLocationIndex maps BlockId {Convert.ToHexString(location.BlockId)} to offset {location.Offset}, " +
                    $"but the block there carries {Convert.ToHexString(block.Header.BlockId)} (corrupt index or misdirected write).");

            var header = block.Header;

            // Re-encryption applies only to DEK-encrypted blocks. The KEK-sealed KeyStore
            // is encrypted with the KEK, not a DEK, so the DEK provider cannot (and must
            // not) touch it — it is copied verbatim and its epochs are pruned separately
            // (US-EMDB-90-6). Everything else stays verbatim.
            bool reEncryptThis =
                reEncryptProvider is not null && header.IsEncrypted && header.Type != BlockType.KeyStore;

            byte[] outPayload;
            ushort outEpoch;
            if (reEncryptThis)
            {
                var reEncrypted = ReEncryptPayload(block, reEncryptProvider!);
                if (reEncrypted.IsFailure)
                    return Result<IReadOnlyList<CopiedBlock>>.Failure(reEncrypted.Error);
                outPayload = reEncrypted.Value;
                outEpoch = reEncryptProvider!.ActiveEpoch;
            }
            else
            {
                outPayload = block.Payload;
                outEpoch = header.KeyEpoch;
            }

            var appended = dest.Append(
                header.Type,
                header.Encoding,
                outPayload,
                header.Compression,
                header.IsEncrypted,
                outEpoch,
                header.BlockId);
            if (appended.IsFailure)
                return Result<IReadOnlyList<CopiedBlock>>.Failure(
                    $"Compaction could not append copied block {Convert.ToHexString(location.BlockId)} to the side file: {appended.Error}");

            // The copy is length-preserving in both modes (a verbatim copy reproduces the
            // block; a re-encrypt only swaps the nonce/ciphertext/tag, whose total size is
            // fixed), so the total length must match what the source index recorded. A
            // mismatch means the index length was wrong or the copy diverged — neither is
            // safe to swap in.
            if (appended.Value.TotalBlockLength != location.TotalBlockLength)
                return Result<IReadOnlyList<CopiedBlock>>.Failure(
                    $"Copied block {Convert.ToHexString(location.BlockId)} changed size: source {location.TotalBlockLength} bytes, " +
                    $"side file {appended.Value.TotalBlockLength} bytes ({(reEncryptThis ? "re-encryption must preserve payload length" : "the copy must be verbatim")}).");

            copied.Add(new CopiedBlock(
                location.BlockId, location.Offset, appended.Value.Offset, appended.Value.TotalBlockLength));
        }

        return Result<IReadOnlyList<CopiedBlock>>.Success(copied);
    }

    /// <summary>
    /// Decrypts a DEK-encrypted block's on-disk payload with its original-epoch DEK and
    /// re-encrypts the plaintext under <paramref name="provider"/>'s active epoch with a
    /// fresh random nonce and AAD recomputed for the new epoch (docs/Compaction.md
    /// Section 4). The BlockId and block type are unchanged, so the ciphertext stays
    /// bound to the block's identity — only the epoch moves. A missing/retired source
    /// epoch or a wrong-key/tamper failure is surfaced as a copy failure, never thrown.
    /// The transient plaintext is scrubbed before returning.
    /// </summary>
    private static Result<byte[]> ReEncryptPayload(Block block, EpochDekProvider provider)
    {
        var header = block.Header;
        byte[] plaintext;
        try
        {
            plaintext = provider.Decrypt(
                block.Payload, header.BlockId, header.Type, header.KeyEpoch);
        }
        catch (EpochDekUnavailableError ex)
        {
            return Result<byte[]>.Failure(
                $"Compaction re-encryption cannot decrypt block {Convert.ToHexString(header.BlockId)} at epoch " +
                $"{header.KeyEpoch}: {ex.Message} (the DEK provider must retain every epoch the live blocks reference).");
        }
        catch (WrongKeyOrTamperError ex)
        {
            return Result<byte[]>.Failure(
                $"Compaction re-encryption failed authenticating block {Convert.ToHexString(header.BlockId)} at epoch " +
                $"{header.KeyEpoch}: {ex.Message}.");
        }
        catch (ArgumentException ex)
        {
            return Result<byte[]>.Failure(
                $"Compaction re-encryption could not decrypt block {Convert.ToHexString(header.BlockId)}: {ex.Message}.");
        }

        try
        {
            return Result<byte[]>.Success(
                provider.Encrypt(plaintext, header.BlockId, header.Type));
        }
        catch (ArgumentException ex)
        {
            return Result<byte[]>.Failure(
                $"Compaction re-encryption could not re-encrypt block {Convert.ToHexString(header.BlockId)} at the active epoch: {ex.Message}.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Writes both superblock slots of a fresh side file, continuing the source's
    /// <c>SuperblockSequence</c> (slot A = source + 1, slot B = source + 2, so the
    /// higher-sequence slot B wins on open — the same two-slot pattern the initialize
    /// protocol uses, spec Section 11.1). The slots clone the source superblock, so
    /// they carry the same FileId, encryption parameters, MaxPayloadLength, and shard
    /// identity; they mark the file in-progress: no committed Checkpoint hint yet and
    /// <c>CleanShutdown = 0</c>.
    /// </summary>
    private static Result WriteFreshSuperblocks(FileStream dest, Superblock source, out ulong continuedSequence)
    {
        continuedSequence = source.SuperblockSequence + 2;

        var fresh = source.Clone();
        fresh.CleanShutdown = 0;
        fresh.LastCheckpointBlockId = new byte[16];
        fresh.LastCheckpointOffset = 0;

        try
        {
            Span<byte> slot = stackalloc byte[SuperblockSerializer.SlotSize];

            fresh.SuperblockSequence = source.SuperblockSequence + 1;
            SuperblockSerializer.Serialize(fresh, slot);
            dest.Seek(SuperblockManager.SlotAOffset, SeekOrigin.Begin);
            dest.Write(slot);

            fresh.SuperblockSequence = source.SuperblockSequence + 2;
            SuperblockSerializer.Serialize(fresh, slot);
            dest.Seek(SuperblockManager.SlotBOffset, SeekOrigin.Begin);
            dest.Write(slot);

            dest.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return Result.Failure(ex.Message);
        }

        return Result.Success();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Releases the side-file and source handles; idempotent, shared by <see cref="FinalizeAndSwap"/>.</summary>
    private void CloseHandles()
    {
        if (_handlesClosed)
            return;
        _handlesClosed = true;
        DestBlockManager.Dispose();
        _destStream.Dispose();
        _source.Dispose();
        _sourceStream.Dispose();
    }

    /// <summary>
    /// Releases the side-file and source handles. On an abandoned compaction (no
    /// successful <see cref="FinalizeAndSwap"/>) this does NOT delete the side file — the
    /// <c>.emdb.compact</c> file simply stays on disk, which the next open discards as
    /// stale via <see cref="CleanupLeftoverSideFile"/> (spec Section 11.2). After a
    /// successful swap the handles are already closed and the side file has been renamed
    /// away, so this is a no-op.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseHandles();
        if (_keyStoreKek is not null)
        {
            CryptographicOperations.ZeroMemory(_keyStoreKek);
            _keyStoreKek = null;
        }
    }
}
