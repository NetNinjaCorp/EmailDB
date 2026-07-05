using System.Buffers.Binary;

namespace EmailDB.Format.V3;

/// <summary>
/// Checkpoint-time batch insert for the BlockLocationIndex (US-EMDB-71-7,
/// EmailDB_FileFormat_Spec.md Section 7, docs/BTree_Index.md Section 3). Blocks
/// appended since the last Checkpoint are not yet in the durable location index —
/// they live in the runtime map. At each Checkpoint this collects that batch,
/// sorts it by BlockId, batch-inserts it into the location index in ONE
/// copy-on-write pass, makes the new nodes durable, and emits a fresh
/// <see cref="LocationIndexRoot"/> (Sequence + 1) for the Checkpoint block to
/// reference. Building the Checkpoint block itself — and clearing the runtime map
/// once that block is durable — is the Checkpoint layer's job (US-EMDB-72); this
/// exposes what it needs via <see cref="CommittedRoot"/> and the emitted descriptor.
///
/// <para><b>Write order</b> (spec Section 10.3): new nodes → fsync → the
/// LocationIndexRoot descriptor → (caller) the Checkpoint block referencing it.
/// The sort-by-BlockId and single COW pass are performed by
/// <see cref="BlockLocationIndex.PutBatch"/>, which touches each shared leaf once.</para>
///
/// <para><b>Failure contract.</b> A node-write failure while applying the batch
/// leaves the location index's <see cref="BlockLocationIndex.Root"/> and this
/// checkpointer's <see cref="CommittedRoot"/> untouched (the previous root stays
/// authoritative) with the partially written nodes as orphans for compaction. An
/// fsync failure is FATAL (spec Section 10.3): the caller MUST poison the file
/// handle and force crash recovery, which reloads the durable Checkpoint's root —
/// so the previous <see cref="CommittedRoot"/> is left unchanged here and the
/// in-memory index root is moot. Either way no new <see cref="LocationIndexRoot"/>
/// is emitted.</para>
///
/// Not thread-safe: a checkpoint mutates the location index in place. Runs at the
/// single writer's Checkpoint commit point.
/// </summary>
public sealed class LocationIndexCheckpoint
{
    private readonly BlockLocationIndex _index;
    private readonly Func<Result> _fsync;
    private LocationIndexRoot? _committedRoot;

    /// <summary>
    /// Creates the checkpointer over one location index.
    /// </summary>
    /// <param name="index">The BlockLocationIndex to batch-insert into at each Checkpoint.</param>
    /// <param name="fsync">Makes the freshly written nodes durable before the root descriptor is emitted (typically <see cref="BlockManager.Flush"/>).</param>
    /// <param name="initialRoot">The location root committed by the last Checkpoint at open, or null before the index has ever held a block.</param>
    public LocationIndexCheckpoint(BlockLocationIndex index, Func<Result> fsync, LocationIndexRoot? initialRoot = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(fsync);
        _index = index;
        _fsync = fsync;
        _committedRoot = initialRoot;
    }

    /// <summary>The last emitted location root descriptor; null before the first block is checkpointed.</summary>
    public LocationIndexRoot? CommittedRoot => _committedRoot;

    /// <summary>The last emitted Sequence, or null before the first block is checkpointed.</summary>
    public ulong? Sequence => _committedRoot?.Sequence;

    /// <summary>
    /// Runs the checkpoint-time batch insert against the entries currently tracked
    /// by <paramref name="runtimeMap"/> — every block appended since the last
    /// Checkpoint. A point-in-time snapshot is taken; blocks appended concurrently
    /// are picked up at the next Checkpoint. The runtime map is NOT cleared here:
    /// the caller clears it only after the Checkpoint block that references the
    /// emitted root is durable (spec Section 7, RuntimeBlockOffsetMap.Clear).
    /// </summary>
    /// <param name="runtimeMap">Source of the blocks-since-last-Checkpoint batch.</param>
    /// <returns>The emitted <see cref="LocationIndexRoot"/> on success — null only when the index is still empty (no blocks yet); failure leaves the previous root authoritative.</returns>
    public Result<LocationIndexRoot?> Checkpoint(RuntimeBlockOffsetMap runtimeMap)
    {
        ArgumentNullException.ThrowIfNull(runtimeMap);
        return Checkpoint(runtimeMap.SnapshotOrderedByOffset());
    }

    /// <summary>
    /// Runs the checkpoint-time batch insert against an explicit batch of blocks
    /// appended since the last Checkpoint (the decoupled form
    /// <see cref="Checkpoint(RuntimeBlockOffsetMap)"/> delegates to). The batch is
    /// sorted by BlockId and applied in one COW pass by
    /// <see cref="BlockLocationIndex.PutBatch"/>; an empty batch is a no-op that
    /// re-returns the current <see cref="CommittedRoot"/> unchanged (the Checkpoint
    /// still references the same location root).
    /// </summary>
    /// <param name="blocksSinceLastCheckpoint">The blocks to fold into the index; duplicates resolve last-wins.</param>
    public Result<LocationIndexRoot?> Checkpoint(IReadOnlyList<BlockLocation> blocksSinceLastCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(blocksSinceLastCheckpoint);

        // Nothing appended since the last Checkpoint: the index is unchanged, so
        // no new nodes, no fsync, and no Sequence bump — the Checkpoint keeps
        // referencing the same location root.
        if (blocksSinceLastCheckpoint.Count == 0)
            return Result<LocationIndexRoot?>.Success(_committedRoot);

        // Sort-by-BlockId single COW pass. On a node-write failure PutBatch leaves
        // the index Root at its pre-batch version (partial nodes are orphans), so
        // the previous location root stays authoritative.
        var batch = _index.PutBatch(blocksSinceLastCheckpoint);
        if (batch.IsFailure)
            return Fail("node write", batch.Error);

        // Make the new nodes durable before the root pointer that references them
        // (spec Section 10.3 write order). fsync failure is fatal (caller poisons).
        var fsynced = _fsync();
        if (fsynced.IsFailure)
            return Fail("fsync", fsynced.Error);

        // The batch was non-empty, so the index now has a real root node.
        var root = _index.Root!;
        long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(root.RootRef.Reference);
        var candidate = _committedRoot is null
            ? LocationIndexRoot.CreateInitial(rootOffset, root.EntryCount, root.Height, root.RootHash)
            : _committedRoot.NextVersion(rootOffset, root.EntryCount, root.Height, root.RootHash);

        // Commit: only now does the new location root become the emitted version.
        _committedRoot = candidate;
        return Result<LocationIndexRoot?>.Success(candidate);
    }

    /// <summary>
    /// Reports a checkpoint failure without advancing <see cref="CommittedRoot"/> —
    /// the previous location root stays authoritative; any nodes written before the
    /// failure are orphans for compaction (spec Sections 7, 10.3).
    /// </summary>
    private static Result<LocationIndexRoot?> Fail(string phase, string error) =>
        Result<LocationIndexRoot?>.Failure(
            $"Location index checkpoint aborted during {phase}: {error} " +
            "Previous location root remains authoritative; partially written nodes are orphans " +
            "for compaction (spec Sections 7, 10.3).");
}
