using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// What an <see cref="CleanOpener"/> open attempt resolved to
/// (EmailDB_FileFormat_Spec.md Section 10.2).
/// </summary>
public enum OpenOutcomeKind
{
    /// <summary>
    /// <c>CleanShutdown = 1</c>: the hinted Checkpoint is current, its roots were
    /// loaded, and the open-state is ready — with ZERO file scanning.
    /// </summary>
    CleanOpen = 0,

    /// <summary>
    /// <c>CleanShutdown = 0</c>: the last session did not close cleanly, so the
    /// bounded dirty scan (newer Checkpoints + unreplayed WAL, spec Section 10.2
    /// step 4) is required. That recovery is a separate component (US-EMDB-74-7);
    /// the clean-open fast path stops here and reports this outcome.
    /// </summary>
    DirtyOpenRequired = 1,

    /// <summary>
    /// The superblock indicates encryption but no <see cref="IEncryptionBootstrap"/>
    /// was supplied. Encryption bootstrap (KEK derivation, KeyStore decrypt, spec
    /// Section 10.2 step 2) is other stories' work; the clean-open path leaves this
    /// seam and refuses to proceed rather than read ciphertext as plaintext.
    /// </summary>
    EncryptionBootstrapRequired = 2,
}

/// <summary>
/// Seam for the encryption bootstrap the open protocol runs between selecting the
/// superblock and loading the Checkpoint (EmailDB_FileFormat_Spec.md Section 10.2
/// step 2: NFC-normalize password → KEK → verify token → decrypt KeyStore). The
/// KeyStore/KDF machinery is out of scope for the clean-open fast path
/// (US-EMDB-74-6); this interface only marks where that logic plugs in. When a file
/// is encrypted and no implementation is supplied, the opener reports
/// <see cref="OpenOutcomeKind.EncryptionBootstrapRequired"/> instead of guessing.
/// </summary>
public interface IEncryptionBootstrap
{
    /// <summary>
    /// Runs the encryption bootstrap for the just-selected superblock. On success
    /// the opener proceeds to load the Checkpoint; on failure the open fails.
    /// </summary>
    /// <param name="superblock">The selected superblock (carries salt, KDF params, key-verification token).</param>
    Result Bootstrap(Superblock superblock);
}

/// <summary>
/// The in-memory state a clean open produces (EmailDB_FileFormat_Spec.md
/// Section 10.2): the selected superblock, the resolved Checkpoint (the commit-point
/// snapshot), and the read-side machinery wired to serve O(log n) lookups — the
/// runtime map (empty: nothing has been appended this session), the live
/// BlockLocationIndex reconstructed from the Checkpoint's location root, and the
/// resolution precedence chain over them (runtime map → location index, spec
/// Section 7). No disaster full-scan resolver is wired: a clean open never scans.
///
/// <para>Disposing releases the block manager (and the node store's cache). The
/// caller owns the underlying stream.</para>
/// </summary>
public sealed class OpenState : IDisposable
{
    private bool _disposed;

    /// <summary>The superblock adopted by slot selection (higher valid sequence).</summary>
    public required Superblock Superblock { get; init; }

    /// <summary>The block manager over the file, its <see cref="MaxPayloadLength"/> from the superblock.</summary>
    public required BlockManager BlockManager { get; init; }

    /// <summary>The validated, FileId-cross-checked Checkpoint with every root resolved to a concrete location.</summary>
    public required ResolvedCheckpoint Checkpoint { get; init; }

    /// <summary>The session's runtime BlockId→offset map — EMPTY on a clean open (nothing appended yet).</summary>
    public required RuntimeBlockOffsetMap RuntimeMap { get; init; }

    /// <summary>The node store the location index (and later BlockId-addressed indexes) read through.</summary>
    public required BTreeNodeStore NodeStore { get; init; }

    /// <summary>
    /// The live BlockLocationIndex reconstructed from the Checkpoint's location root
    /// (offset-addressed). Its <see cref="BlockLocationIndex.Root"/> is null when the
    /// Checkpoint names no location root (a file with no live blocks yet).
    /// </summary>
    public required BlockLocationIndex LocationIndex { get; init; }

    /// <summary>The resolution precedence chain: runtime map → location index (spec Section 7). No disaster scan.</summary>
    public required CompositeBlockIdResolver Resolver { get; init; }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        BlockManager.Dispose();
    }
}

/// <summary>
/// The result of an <see cref="CleanOpener.Open"/> attempt: the
/// <see cref="Kind"/> plus, for a <see cref="OpenOutcomeKind.CleanOpen"/>, the
/// ready-to-use <see cref="State"/>. Non-clean outcomes carry a human-readable
/// <see cref="Detail"/> and no state.
/// </summary>
public sealed class CleanOpenResult
{
    /// <summary>What the open resolved to.</summary>
    public required OpenOutcomeKind Kind { get; init; }

    /// <summary>The open-state, present only when <see cref="Kind"/> is <see cref="OpenOutcomeKind.CleanOpen"/>.</summary>
    public OpenState? State { get; init; }

    /// <summary>Human-readable context for a non-clean outcome; null for a clean open.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The clean-open fast path (EmailDB_FileFormat_Spec.md Section 10.2 steps 1-3, 5):
/// select the valid superblock, and when <c>CleanShutdown = 1</c> follow its
/// LastCheckpoint hint to load the Checkpoint (FileId cross-check), load the index
/// roots it names — including the offset-addressed BlockLocationIndex root — and
/// construct the read-side open-state, performing <b>zero file scanning</b>.
///
/// <para><b>Zero scanning, provably.</b> This class only ever issues targeted block
/// reads: two superblock slots, the one hinted Checkpoint block, one verifying read
/// per named root (<see cref="CheckpointReader"/>), and the O(height) leftmost-spine
/// reads that reconstruct the location index root. It NEVER calls
/// <see cref="BlockManager.ScanForward"/>, <see cref="BlockManager.FindLastValidBlock"/>,
/// or any file walk. Because every read is a discrete block read (never a
/// megabyte-scale sequential chunk), a test wrapping the file stream can prove the
/// open touched only O(log n) bytes, not O(file) — the acceptance criterion "clean
/// open performs zero scanning beyond superblock and checkpoint reads".</para>
///
/// <para><b>Seams left for other stories.</b> <c>CleanShutdown = 0</c> yields
/// <see cref="OpenOutcomeKind.DirtyOpenRequired"/> — the bounded dirty scan is
/// US-EMDB-74-7 and is deliberately NOT built here. An encrypted file with no
/// <see cref="IEncryptionBootstrap"/> yields
/// <see cref="OpenOutcomeKind.EncryptionBootstrapRequired"/> — crypto is other
/// stories' work. The disaster full-scan fallbacks (no valid superblock, no valid
/// Checkpoint — spec Section 10.2 step 6) are surfaced as failures here, not
/// silently scanned.</para>
/// </summary>
public static class CleanOpener
{
    /// <summary>
    /// Runs the clean-open fast path over <paramref name="stream"/> (spec Section
    /// 10.2). The caller owns <paramref name="stream"/>; a
    /// <see cref="OpenOutcomeKind.CleanOpen"/> result's <see cref="OpenState"/> owns
    /// only the block manager it created (dispose it to release that).
    /// </summary>
    /// <param name="stream">A readable, writable, seekable stream over the EmailDB file.</param>
    /// <param name="encryptionBootstrap">
    /// Optional encryption bootstrap seam (spec Section 10.2 step 2). Required to open
    /// an encrypted file; omit for plaintext files.
    /// </param>
    /// <returns>
    /// Success carrying the outcome; a failure only for a genuine open error (no valid
    /// superblock, a torn/foreign Checkpoint, an unresolvable root) — never for the
    /// expected dirty/encryption seams, which are success results with the matching
    /// <see cref="OpenOutcomeKind"/>.
    /// </returns>
    public static Result<CleanOpenResult> Open(
        FileStream stream, IEncryptionBootstrap? encryptionBootstrap = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // 1. Select the valid superblock (higher valid sequence wins). This reads
        //    only the two fixed 4096-byte slots — never the block stream.
        Superblock superblock;
        using (var superblockManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = superblockManager.Load();
            if (loaded.IsFailure)
                // No valid superblock -> the disaster full-scan fallback (spec Section
                // 10.2 step 6) is out of scope for the fast path; surface the failure.
                return Result<CleanOpenResult>.Failure(
                    $"Clean open failed: no valid superblock (a full-scan rebuild is the disaster fallback, spec Section 10.2 step 6). {loaded.Error}");
            superblock = loaded.Value;
        }

        // Unknown IncompatFlags must refuse the open (spec Section 10.2 step 1).
        var unknownIncompat = SuperblockFeatureFlags.UnknownIncompatBits(superblock);
        if (unknownIncompat != 0)
            return Result<CleanOpenResult>.Failure(
                $"Clean open refused: superblock carries unknown IncompatFlags 0x{unknownIncompat:X8}; this build cannot open the file (spec Section 10.2 step 1).");

        // 3'. CleanShutdown gate. CleanShutdown = 0 -> the bounded dirty scan
        //     (US-EMDB-74-7) is required; the fast path reports and stops here.
        if (superblock.CleanShutdown != 1)
            return Result<CleanOpenResult>.Success(new CleanOpenResult
            {
                Kind = OpenOutcomeKind.DirtyOpenRequired,
                Detail = "Superblock CleanShutdown = 0: the last session did not close cleanly; run the bounded dirty scan (spec Section 10.2 step 4).",
            });

        // 2. Encryption bootstrap seam (spec Section 10.2 step 2). Ciphertext must
        //    not be read as plaintext, so without a bootstrap this is a reported seam.
        if (superblock.EncryptionEnabled != 0)
        {
            if (encryptionBootstrap is null)
                return Result<CleanOpenResult>.Success(new CleanOpenResult
                {
                    Kind = OpenOutcomeKind.EncryptionBootstrapRequired,
                    Detail = "Superblock marks the file encrypted but no IEncryptionBootstrap was supplied (KeyStore/KDF is other stories' work, spec Section 10.2 step 2).",
                });

            var bootstrapped = encryptionBootstrap.Bootstrap(superblock);
            if (bootstrapped.IsFailure)
                return Result<CleanOpenResult>.Failure(
                    $"Clean open failed: encryption bootstrap failed (spec Section 10.2 step 2). {bootstrapped.Error}");
        }

        // A clean superblock with no Checkpoint hint means a file created but never
        // checkpointed. There is nothing to load and nothing to scan.
        if (IsAllZero(superblock.LastCheckpointBlockId))
            return Result<CleanOpenResult>.Failure(
                "Clean open failed: the superblock records no LastCheckpoint hint; the file has no committed Checkpoint (a full-scan rebuild is the disaster fallback, spec Section 10.2 step 6).");

        // Build the block manager (its length sanity bound comes from the superblock)
        // over an EMPTY runtime map: a clean open has appended nothing this session.
        var runtimeMap = new RuntimeBlockOffsetMap();
        var manager = new BlockManager(
            stream,
            maxPayloadLength: superblock.MaxPayloadLength,
            offsetMap: runtimeMap,
            ownsStream: false);

        var built = BuildCleanState(manager, runtimeMap, superblock);
        if (built.IsFailure)
        {
            manager.Dispose();
            return Result<CleanOpenResult>.Failure(built.Error);
        }

        return Result<CleanOpenResult>.Success(new CleanOpenResult
        {
            Kind = OpenOutcomeKind.CleanOpen,
            State = built.Value,
        });
    }

    private static Result<OpenState> BuildCleanState(
        BlockManager manager, RuntimeBlockOffsetMap runtimeMap, Superblock superblock)
    {
        // 3. Follow the LastCheckpoint hint and load the Checkpoint (FileId
        //    cross-check inside Open). The resolver is the empty runtime map: on a
        //    clean open every offset hint is fresh, so no re-resolution occurs and
        //    NO disaster scan is wired (a stale hint would be a genuine failure, not
        //    a scan). This reads exactly the Checkpoint block and one verifying read
        //    per named root — no file walk.
        var reader = new CheckpointReader(manager, runtimeMap);
        var opened = reader.Open(superblock.LastCheckpointOffset, superblock.FileId);
        if (opened.IsFailure)
            return Result<OpenState>.Failure(
                $"Clean open failed loading the hinted Checkpoint at offset {superblock.LastCheckpointOffset}: {opened.Error}");

        var resolvedCheckpoint = opened.Value;

        // Cross-check the hinted BlockId too: the superblock names both the offset and
        // the ULID of its LastCheckpoint (spec Section 3.1). CheckpointReader.Open
        // verified the block is a well-formed Checkpoint carrying our FileId; confirm
        // it is the very block the superblock pointed at.
        if (!superblock.LastCheckpointBlockId.AsSpan().SequenceEqual(
                CheckpointBlockIdAt(manager, superblock.LastCheckpointOffset)))
            return Result<OpenState>.Failure(
                "Clean open failed: the block at the superblock's LastCheckpoint offset does not carry the hinted Checkpoint BlockId (spec Section 10.2 step 3).");

        // 5. Reconstruct the live BlockLocationIndex from the Checkpoint's location
        //    root (offset-addressed). Offset-addressed nodes need no BlockId resolver.
        var nodeStore = new BTreeNodeStore(manager, blockIdResolver: null);
        var locationIndex = ReconstructLocationIndex(manager, nodeStore, resolvedCheckpoint);
        if (locationIndex.IsFailure)
            return Result<OpenState>.Failure(locationIndex.Error);

        // Wire the resolution precedence chain (spec Section 7): runtime map (empty)
        // first, then the live location index. No disaster full scan — a clean open
        // must never fall through to O(file).
        var resolver = CompositeBlockIdResolver.Create(runtimeMap, locationIndex.Value, disasterScan: null);

        return Result<OpenState>.Success(new OpenState
        {
            Superblock = superblock,
            BlockManager = manager,
            Checkpoint = resolvedCheckpoint,
            RuntimeMap = runtimeMap,
            NodeStore = nodeStore,
            LocationIndex = locationIndex.Value,
            Resolver = resolver,
        });
    }

    /// <summary>
    /// Reconstructs a live <see cref="BlockLocationIndex"/> from the Checkpoint's
    /// resolved location root. The Checkpoint records the location index by the file
    /// offset of its B+-tree root node (it IS the offset map, so it cannot address
    /// its own root by ULID — spec Section 7). This reads that root node to recover
    /// its Merkle content hash and walks the leftmost spine to recover the tree
    /// height — O(height) targeted reads, never a scan. The tree's total entry count
    /// is the Checkpoint's <see cref="Checkpoint.LiveBlockCount"/> (the location index
    /// holds exactly one entry per live block, spec Sections 7, 10.1).
    /// </summary>
    private static Result<BlockLocationIndex> ReconstructLocationIndex(
        BlockManager manager, BTreeNodeStore nodeStore, ResolvedCheckpoint checkpoint)
    {
        var locationRoot = checkpoint.LocationIndexRoot;
        if (locationRoot is null)
            // The Checkpoint names no location root: an empty index (no live blocks).
            return Result<BlockLocationIndex>.Success(new BlockLocationIndex(nodeStore));

        var shape = ReadRootShape(manager, locationRoot.Offset);
        if (shape.IsFailure)
            return Result<BlockLocationIndex>.Failure(
                $"Clean open failed reconstructing the BlockLocationIndex root: {shape.Error}");

        var reference = new byte[BTreeNodeRef.OffsetReferenceSize];
        BinaryPrimitives.WriteInt64LittleEndian(reference, locationRoot.Offset);

        var btreeRoot = new BTreeRoot
        {
            RootRef = new BTreeNodeRef
            {
                Addressing = BTreeChildAddressing.Offset,
                Reference = reference,
                NodeHash = shape.Value.RootHash,
            },
            Height = shape.Value.Height,
            EntryCount = checkpoint.Checkpoint.LiveBlockCount,
        };

        return Result<BlockLocationIndex>.Success(new BlockLocationIndex(nodeStore, initialRoot: btreeRoot));
    }

    private readonly record struct RootShape(int Height, byte[] RootHash);

    /// <summary>
    /// Reads the location index root node and descends its leftmost spine to derive
    /// the tree height and the root node's content hash — all offset-addressed reads,
    /// bounded by tree height. No scanning.
    /// </summary>
    private static Result<RootShape> ReadRootShape(BlockManager manager, long rootOffset)
    {
        byte[]? rootHash = null;
        long offset = rootOffset;
        int height = 0;

        // Guard against a corrupt spine that never reaches a leaf: a tree can be no
        // taller than the number of nodes in the file, and each hop reads a distinct
        // node deeper in the tree. Cap generously to avoid an unbounded loop on a
        // cyclic/corrupt child offset.
        const int maxSpineDepth = 4096;
        while (true)
        {
            if (height >= maxSpineDepth)
                return Result<RootShape>.Failure(
                    $"BlockLocationIndex spine exceeded {maxSpineDepth} levels descending from offset {rootOffset} (corrupt child offset).");

            var block = manager.ReadDecompressed(offset);
            if (block.IsFailure)
                return Result<RootShape>.Failure(
                    $"reading the node at offset {offset}: {block.Error}");

            var payload = block.Value.Payload;
            var header = BTreeNodeSerializer.DeserializeHeader(payload);
            if (header.IsFailure)
                return Result<RootShape>.Failure(
                    $"parsing the node header at offset {offset}: {header.Error}");

            rootHash ??= BTreeNodeSerializer.ComputeNodeContentHash(payload);
            height++;

            if (header.Value.NodeKind == BTreeNodeKind.Leaf)
                return Result<RootShape>.Success(new RootShape(height, rootHash));

            var node = BTreeNodeSerializer.DeserializeInternal(payload);
            if (node.IsFailure)
                return Result<RootShape>.Failure(
                    $"parsing the internal node at offset {offset}: {node.Error}");
            if (node.Value.Children.Count == 0)
                return Result<RootShape>.Failure(
                    $"internal node at offset {offset} has no children (corrupt node).");

            // Leftmost child record is Reference (8-byte offset) ‖ ChildHash (32);
            // descend by its ChildOffset.
            var childRecord = node.Value.Children[0];
            if (childRecord.Length < BTreeNodeRef.OffsetReferenceSize)
                return Result<RootShape>.Failure(
                    $"internal node at offset {offset} has a truncated child record (corrupt node).");
            offset = BinaryPrimitives.ReadInt64LittleEndian(
                childRecord.AsSpan(0, BTreeNodeRef.OffsetReferenceSize));
            if (offset < 0)
                return Result<RootShape>.Failure(
                    $"internal node child offset {offset} is negative (corrupt node).");
        }
    }

    /// <summary>Reads the BlockId of the block at <paramref name="offset"/>, or an empty array on any failure.</summary>
    private static byte[] CheckpointBlockIdAt(BlockManager manager, long offset)
    {
        var block = manager.Read(offset);
        return block.IsSuccess ? block.Value.Header.BlockId : Array.Empty<byte>();
    }

    private static bool IsAllZero(byte[] value)
    {
        foreach (byte b in value)
        {
            if (b != 0)
                return false;
        }
        return true;
    }
}
