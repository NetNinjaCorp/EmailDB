namespace EmailDB.Format.V3;

/// <summary>
/// Loads and validates a Checkpoint block — the single commit point of a v3 file —
/// and resolves the roots it names (EmailDB_FileFormat_Spec.md Sections 10.1, 10.2,
/// 13). The counterpart of <see cref="CheckpointWriter"/>.
///
/// <para><b>Validation.</b> <see cref="Load"/> reads the block through
/// <see cref="BlockManager.Read"/> (header checksum before any field is trusted,
/// payload checksum, footer) and deserializes with
/// <see cref="CheckpointSerializer.Deserialize"/> (structural bounds check), so a
/// torn or corrupt Checkpoint is a failure, never returned as valid.
/// <see cref="Open"/> additionally cross-checks the payload's
/// <see cref="Checkpoint.FileId"/> against the file identity (the superblock's
/// FileId): a mismatch means the block does not belong to this file and is rejected
/// (spec Section 10.1 "FileId ... must match superblock").</para>
///
/// <para><b>Offset hints are verified, not trusted.</b> Every root is a ULID+offset
/// pair; the ULID is authoritative and the offset only a hint. On use the reader
/// reads the block at the hint and confirms it carries the expected BlockId. A
/// mismatch (stale hint after compaction, or a misdirected write) is NOT an error:
/// the reader silently re-resolves the BlockId through the normal resolution chain
/// (runtime map -> BlockLocationIndex -> disaster scan) and records the result with
/// <see cref="ResolvedRoot.HintWasStale"/> = true. Only a root that neither the
/// hint nor the resolution chain can locate is a genuine failure.</para>
///
/// <para><b>Chain walk.</b> <see cref="WalkChain"/> follows the
/// <see cref="Checkpoint.PreviousCheckpoint"/> links newest-to-oldest, verifying
/// each hop the same way and asserting the sequence strictly decreases, until it
/// reaches the first Checkpoint (<see cref="CheckpointRootPointer.None"/>).</para>
/// </summary>
public sealed class CheckpointReader
{
    private readonly BlockManager _manager;
    private readonly IBlockIdResolver _resolver;

    /// <summary>Creates a reader over one file's blocks and its BlockId resolution chain.</summary>
    /// <param name="manager">The block manager the Checkpoint and its roots are read through.</param>
    /// <param name="resolver">The resolution chain used to re-resolve a root whose offset hint is stale (spec Section 7).</param>
    public CheckpointReader(BlockManager manager, IBlockIdResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(resolver);
        _manager = manager;
        _resolver = resolver;
    }

    /// <summary>
    /// Reads and validates the Checkpoint block at <paramref name="offset"/>. A torn
    /// or corrupt block, a non-Checkpoint block, or a structurally invalid payload
    /// yields a failure — never a bad commit point.
    /// </summary>
    /// <param name="offset">File offset of the Checkpoint block's first header byte.</param>
    public Result<Checkpoint> Load(long offset)
    {
        var block = _manager.Read(offset);
        if (block.IsFailure)
            return Result<Checkpoint>.Failure(
                $"Checkpoint block at offset {offset} could not be read (torn or corrupt): {block.Error}");

        if (block.Value.Header.Type != BlockType.Checkpoint)
            return Result<Checkpoint>.Failure(
                $"Block at offset {offset} is type {block.Value.Header.Type}, not a Checkpoint (type {(int)BlockType.Checkpoint}).");

        return CheckpointSerializer.Deserialize(block.Value.Payload);
    }

    /// <summary>
    /// Loads the Checkpoint at <paramref name="offset"/>, cross-checks its FileId
    /// against <paramref name="expectedFileId"/>, and resolves every named root
    /// (offset hint verified by BlockId, re-resolved on mismatch).
    /// </summary>
    /// <param name="offset">File offset of the Checkpoint block's first header byte.</param>
    /// <param name="expectedFileId">The file's 16-byte ULID from the superblock.</param>
    public Result<ResolvedCheckpoint> Open(long offset, byte[] expectedFileId)
    {
        ArgumentNullException.ThrowIfNull(expectedFileId);
        if (expectedFileId.Length != Checkpoint.FileIdSize)
            throw new ArgumentException(
                $"{nameof(expectedFileId)} must be exactly {Checkpoint.FileIdSize} bytes, got {expectedFileId.Length}.",
                nameof(expectedFileId));

        var loaded = Load(offset);
        if (loaded.IsFailure)
            return Result<ResolvedCheckpoint>.Failure(loaded.Error);

        var checkpoint = loaded.Value;
        if (!checkpoint.FileId.AsSpan().SequenceEqual(expectedFileId))
            return Result<ResolvedCheckpoint>.Failure(
                $"Checkpoint at offset {offset} carries FileId {Hex(checkpoint.FileId)} but the file identity is " +
                $"{Hex(expectedFileId)}; the Checkpoint does not belong to this file (spec Section 10.1).");

        var folder = ResolveRoot(checkpoint.FolderTreeRoot, "FolderTreeRoot");
        if (folder.IsFailure) return Result<ResolvedCheckpoint>.Failure(folder.Error);
        var primary = ResolveRoot(checkpoint.PrimaryIndexRoot, "PrimaryIndexRoot");
        if (primary.IsFailure) return Result<ResolvedCheckpoint>.Failure(primary.Error);
        var location = ResolveRoot(checkpoint.LocationIndexRoot, "LocationIndexRoot");
        if (location.IsFailure) return Result<ResolvedCheckpoint>.Failure(location.Error);
        var metadata = ResolveRoot(checkpoint.MetadataRoot, "MetadataRoot");
        if (metadata.IsFailure) return Result<ResolvedCheckpoint>.Failure(metadata.Error);
        var keyStore = ResolveRoot(checkpoint.KeyStoreRoot, "KeyStoreRoot");
        if (keyStore.IsFailure) return Result<ResolvedCheckpoint>.Failure(keyStore.Error);
        var previous = ResolveRoot(checkpoint.PreviousCheckpoint, "PreviousCheckpoint");
        if (previous.IsFailure) return Result<ResolvedCheckpoint>.Failure(previous.Error);

        var secondaries = new ResolvedSecondaryRoot[checkpoint.SecondaryIndexes.Count];
        for (int i = 0; i < secondaries.Length; i++)
        {
            var entry = checkpoint.SecondaryIndexes[i];
            var resolved = ResolveRoot(entry.Pointer, $"SecondaryIndexes[{i}] ({entry.IndexKind})");
            if (resolved.IsFailure) return Result<ResolvedCheckpoint>.Failure(resolved.Error);
            // A secondary-index entry is always a real root (the table records only
            // present indexes), so resolution yields a non-null location.
            secondaries[i] = new ResolvedSecondaryRoot
            {
                IndexKind = entry.IndexKind,
                Root = resolved.Value!,
            };
        }

        return Result<ResolvedCheckpoint>.Success(new ResolvedCheckpoint
        {
            Checkpoint = checkpoint,
            FolderTreeRoot = folder.Value,
            PrimaryIndexRoot = primary.Value,
            LocationIndexRoot = location.Value,
            MetadataRoot = metadata.Value,
            KeyStoreRoot = keyStore.Value,
            PreviousCheckpoint = previous.Value,
            SecondaryIndexes = secondaries,
        });
    }

    /// <summary>
    /// Walks the previous-Checkpoint chain from the Checkpoint at
    /// <paramref name="newestOffset"/> newest-to-oldest, returning every Checkpoint
    /// in that order. Each hop is validated and FileId-cross-checked like
    /// <see cref="Open"/>; the sequence MUST strictly decrease along the chain, and
    /// a repeated offset (a cycle) is rejected. The walk stops at the first
    /// Checkpoint (<see cref="CheckpointRootPointer.None"/> previous link).
    /// </summary>
    /// <param name="newestOffset">Offset of the newest Checkpoint to start from.</param>
    /// <param name="expectedFileId">The file's 16-byte ULID from the superblock.</param>
    public Result<IReadOnlyList<Checkpoint>> WalkChain(long newestOffset, byte[] expectedFileId)
    {
        ArgumentNullException.ThrowIfNull(expectedFileId);

        var chain = new List<Checkpoint>();
        var visitedOffsets = new HashSet<long>();

        long offset = newestOffset;
        while (true)
        {
            if (!visitedOffsets.Add(offset))
                return Result<IReadOnlyList<Checkpoint>>.Failure(
                    $"Checkpoint chain revisits offset {offset}: the previous-Checkpoint links form a cycle (corrupt chain).");

            var opened = Open(offset, expectedFileId);
            if (opened.IsFailure)
                return Result<IReadOnlyList<Checkpoint>>.Failure(
                    $"Checkpoint chain walk failed at offset {offset}: {opened.Error}");

            var resolved = opened.Value;
            if (chain.Count > 0 && resolved.Checkpoint.CheckpointSequence >= chain[^1].CheckpointSequence)
                return Result<IReadOnlyList<Checkpoint>>.Failure(
                    $"Checkpoint chain is not monotonic: sequence {resolved.Checkpoint.CheckpointSequence} at offset " +
                    $"{offset} is not strictly less than its successor's {chain[^1].CheckpointSequence} (corrupt chain).");

            chain.Add(resolved.Checkpoint);

            if (resolved.PreviousCheckpoint is null)
                return Result<IReadOnlyList<Checkpoint>>.Success(chain);

            offset = resolved.PreviousCheckpoint.Offset;
        }
    }

    /// <summary>
    /// Resolves one root pointer to a concrete location: verify the offset hint by
    /// BlockId, and on mismatch silently re-resolve through the resolution chain
    /// (spec Section 10.1). Absent pointers resolve to null.
    /// </summary>
    private Result<ResolvedRoot?> ResolveRoot(CheckpointRootPointer pointer, string name)
    {
        if (pointer.IsAbsent)
            return Result<ResolvedRoot?>.Success(null);

        // 1. Trust the offset hint only if the block there carries the expected BlockId.
        var atHint = _manager.Read(pointer.Offset);
        if (atHint.IsSuccess && atHint.Value.Header.BlockId.AsSpan().SequenceEqual(pointer.BlockId))
        {
            return Result<ResolvedRoot?>.Success(new ResolvedRoot
            {
                BlockId = (byte[])pointer.BlockId.Clone(),
                Offset = pointer.Offset,
                TotalBlockLength = BlockSerializer.GetTotalBlockLength(atHint.Value.Header.PayloadLength),
                HintWasStale = false,
            });
        }

        // 2. Stale/misdirected hint: re-resolve by BlockId through the normal chain.
        //    A stale hint is never an error by itself (spec Section 10.1).
        if (_resolver.TryGetLocation(pointer.BlockId, out var location) && location is not null)
        {
            return Result<ResolvedRoot?>.Success(new ResolvedRoot
            {
                BlockId = (byte[])pointer.BlockId.Clone(),
                Offset = location.Offset,
                TotalBlockLength = location.TotalBlockLength,
                HintWasStale = true,
            });
        }

        // 3. Neither the hint nor the resolution chain can locate the root: this is a
        //    genuinely unreachable root, not a mere stale hint.
        return Result<ResolvedRoot?>.Failure(
            $"Checkpoint {name} block {Hex(pointer.BlockId)} could not be resolved: the offset hint {pointer.Offset} " +
            "does not carry it and the resolution chain has no entry for it.");
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value);
}
