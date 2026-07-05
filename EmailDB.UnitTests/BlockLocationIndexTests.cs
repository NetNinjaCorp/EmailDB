using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="BlockLocationIndex"/> — the offset-addressed location
/// tree variant (US-EMDB-71-6, EmailDB_FileFormat_Spec.md Section 7, IndexKind 1):
/// the typed BlockId → (Offset, Length) facade over the generic offset-addressed
/// <see cref="CowBTree"/> whose internal nodes address children by ChildOffset,
/// not ChildBlockId. The node store is deliberately constructed WITHOUT an
/// <see cref="IBlockIdResolver"/> so every read exercises pure offset resolution —
/// the index cannot depend on itself (spec Section 7).
/// </summary>
public class BlockLocationIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-location-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public BlockLocationIndexTests()
    {
        var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        _manager = new BlockManager(stream, offsetMap: _offsetMap, firstBlockOffset: 0, ownsStream: true);
        // No IBlockIdResolver: an offset-addressed tree must resolve every node
        // purely by ChildOffset — it is the offset map and cannot depend on one.
        _store = new BTreeNodeStore(_manager, blockIdResolver: null);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ------------------------------------------------------------- Helpers

    /// <summary>Tiny fan-out so a handful of blocks forces internal levels.</summary>
    private BlockLocationIndex CreateIndex(int maxLeafEntries = 4, int maxInternalKeys = 3) =>
        new(_store, maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

    /// <summary>A 16-byte BlockId whose lexicographic order equals the numeric order of <paramref name="i"/>.</summary>
    private static byte[] BlockId(int i)
    {
        var id = new byte[BlockLocationIndex.BlockIdKeySize];
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), i);
        return id;
    }

    private static void Ok(Result result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
    private static void Ok<T>(Result<T> result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    private void AssertLocation(BlockLocationIndex index, int i, long offset, long length)
    {
        var lookup = index.Lookup(BlockId(i));
        Ok(lookup);
        Assert.True(lookup.Value.Found, $"Block {i} should be present.");
        Assert.Equal(offset, lookup.Value.Offset);
        Assert.Equal(length, lookup.Value.Length);
    }

    private void AssertAbsent(BlockLocationIndex index, int i)
    {
        var lookup = index.Lookup(BlockId(i));
        Ok(lookup);
        Assert.False(lookup.Value.Found, $"Block {i} should be absent.");
    }

    // --------------------------------------------------------------- Tests

    [Fact]
    public void Put_then_lookup_roundtrips_offset_and_length()
    {
        var index = CreateIndex();
        Ok(index.Put(BlockId(1), offset: 4096, length: 512));
        AssertLocation(index, 1, 4096, 512);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Lookup_of_absent_block_is_success_not_found()
    {
        var index = CreateIndex();
        AssertAbsent(index, 42); // empty tree
        Ok(index.Put(BlockId(1), 10, 20));
        AssertAbsent(index, 42); // present tree, missing key
    }

    [Fact]
    public void Put_replaces_existing_location_without_growing_count()
    {
        var index = CreateIndex();
        Ok(index.Put(BlockId(7), 100, 200));
        Ok(index.Put(BlockId(7), 999, 888));
        AssertLocation(index, 7, 999, 888);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Round_trips_full_width_offset_and_length()
    {
        var index = CreateIndex();
        long bigOffset = long.MaxValue - 3;
        long bigLength = long.MaxValue / 2;
        Ok(index.Put(BlockId(1), bigOffset, bigLength));
        AssertLocation(index, 1, bigOffset, bigLength);
    }

    [Fact]
    public void Delete_removes_the_entry_and_reports_removal()
    {
        var index = CreateIndex();
        Ok(index.Put(BlockId(1), 10, 20));

        var deleted = index.Delete(BlockId(1));
        Ok(deleted);
        Assert.True(deleted.Value);
        AssertAbsent(index, 1);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Delete_of_absent_block_is_a_noop_success()
    {
        var index = CreateIndex();
        var deletedEmpty = index.Delete(BlockId(1));
        Ok(deletedEmpty);
        Assert.False(deletedEmpty.Value);

        Ok(index.Put(BlockId(1), 10, 20));
        var deletedMissing = index.Delete(BlockId(2));
        Ok(deletedMissing);
        Assert.False(deletedMissing.Value);
        AssertLocation(index, 1, 10, 20); // untouched
    }

    [Fact]
    public void Internal_nodes_are_offset_addressed_with_forty_byte_child_records()
    {
        var index = CreateIndex();
        Assert.Equal(BTreeChildAddressing.Offset, index.Tree.Addressing);
        // ChildOffset (8) + ChildHash (32) = 40, not ChildBlockId's 48 (spec 6.1).
        Assert.Equal(40, index.Tree.ChildRecordSize);

        // Grow past one leaf so a real internal level exists, then confirm the
        // root reference is an 8-byte raw offset (ChildOffset), never a 16-byte
        // BlockId.
        for (int i = 0; i < 20; i++)
            Ok(index.Put(BlockId(i), offset: 1000 + i, length: 64));
        Assert.NotNull(index.Root);
        Assert.True(index.Root!.Height > 1, "Expected a multi-level tree.");
        Assert.Equal(BTreeChildAddressing.Offset, index.Root.RootRef.Addressing);
        Assert.Equal(BTreeNodeRef.OffsetReferenceSize, index.Root.RootRef.Reference.Length);
    }

    [Fact]
    public void Serialized_internal_nodes_encode_forty_byte_child_offset_records_on_disk()
    {
        // Byte-level proof of the acceptance criterion (spec Section 6.1,
        // IndexKind 1): the SERIALIZED internal-node blocks on disk must carry
        // ChildOffset (8) + ChildHash (32) = 40-byte child records, NOT
        // ChildBlockId (16) + ChildHash (32) = 48-byte records. We read the raw
        // block payloads back and dissect the node header and every child record
        // rather than trusting the tree's configured widths.
        var index = CreateIndex();
        for (int i = 0; i < 40; i++)
            Ok(index.Put(BlockId(i), offset: 4096L * (i + 1), length: 100 + i));

        Assert.NotNull(index.Root);
        Assert.True(index.Root!.Height > 1, "Need a real internal level to inspect.");

        int internalNodesInspected = 0;
        int childRecordsInspected = 0;
        InspectOffsetAddressing(
            index.Root.RootRef.Reference, index.Root.Height,
            ref internalNodesInspected, ref childRecordsInspected);

        Assert.True(internalNodesInspected > 0, "Expected to inspect at least one internal node.");
        Assert.True(childRecordsInspected > 0, "Expected to inspect at least one child record.");
    }

    /// <summary>
    /// Reads the block at <paramref name="reference"/> (an 8-byte little-endian
    /// ChildOffset) straight from the block layer and, for internal nodes,
    /// asserts the raw serialized bytes use offset addressing: NodeKind Internal,
    /// IndexKind BlockLocation, KeySize 16, and a 40-byte child-record ValueSize
    /// (never the 48-byte ChildBlockId width). Then recurses into every child
    /// via its embedded 8-byte offset — a BlockId-addressed record could not be
    /// dereferenced this way (and this store has no resolver). Leaves are
    /// confirmed to be BTreeLeaf blocks so the walk actually reaches the bottom.
    /// </summary>
    private void InspectOffsetAddressing(
        byte[] reference, int height, ref int internalNodes, ref int childRecords)
    {
        // A ChildOffset reference is exactly 8 bytes; a ChildBlockId is 16.
        Assert.Equal(BTreeNodeRef.OffsetReferenceSize, reference.Length);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(reference);
        Assert.True(offset >= 0, "ChildOffset must be a non-negative file offset.");

        var block = _manager.ReadDecompressed(offset);
        Ok(block);
        var payload = block.Value.Payload;

        if (height <= 1)
        {
            Assert.Equal(BlockType.BTreeLeaf, block.Value.Header.Type);
            return;
        }

        Assert.Equal(BlockType.BTreeInternal, block.Value.Header.Type);

        // Node header (spec Section 6): NodeKind(0,1) NodeVersion(1,1)
        // IndexKind(2,2) KeySize(4,1) ValueSize(5,2) EntryCount(7,2).
        Assert.Equal((byte)BTreeNodeKind.Internal, payload[0]);
        int indexKind = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
        int keySize = payload[4];
        int valueSize = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5, 2));
        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(7, 2));

        Assert.Equal((int)BTreeIndexKind.BlockLocation, indexKind);
        Assert.Equal(BlockLocationIndex.BlockIdKeySize, keySize);
        // THE criterion: child records are offset-addressed (8+32=40), not
        // BlockId-addressed (16+32=48).
        Assert.Equal(BTreeNodeRef.OffsetReferenceSize + 32, valueSize);
        Assert.Equal(40, valueSize);
        Assert.NotEqual(48, valueSize);

        // The declared shape must account for the payload exactly, so the
        // 40-byte child stride is not just a header claim but the real layout.
        int expectedLength = 12 + entryCount * keySize + (entryCount + 1) * valueSize;
        Assert.Equal(expectedLength, payload.Length);

        internalNodes++;

        int childBase = 12 + entryCount * keySize;
        for (int c = 0; c <= entryCount; c++)
        {
            var record = payload.AsSpan(childBase + c * valueSize, valueSize);
            // First 8 bytes are the ChildOffset; the trailing 32 are ChildHash.
            var childReference = record[..BTreeNodeRef.OffsetReferenceSize].ToArray();
            childRecords++;
            InspectOffsetAddressing(childReference, height - 1, ref internalNodes, ref childRecords);
        }
    }

    [Fact]
    public void Resolves_a_multi_level_tree_with_no_blockid_resolver()
    {
        // The store has a null IBlockIdResolver, so success here proves every
        // node was reached purely by ChildOffset (spec Section 7: the index
        // cannot depend on itself).
        var index = CreateIndex();
        const int n = 200;
        for (int i = 0; i < n; i++)
            Ok(index.Put(BlockId(i), offset: 8192L * (i + 1), length: 128 + i));

        Assert.True(index.Root!.Height > 2, "Expected several internal levels.");
        for (int i = 0; i < n; i++)
            AssertLocation(index, i, 8192L * (i + 1), 128 + i);
        Assert.Equal(n, index.Count);
    }

    [Fact]
    public void PutBatch_inserts_all_entries_in_one_pass()
    {
        var index = CreateIndex();
        var batch = new List<BlockLocation>();
        for (int i = 0; i < 50; i++)
            batch.Add(new BlockLocation { BlockId = BlockId(i), Offset = 4096L * i, TotalBlockLength = 256 });

        Ok(index.PutBatch(batch));
        Assert.Equal(50, index.Count);
        for (int i = 0; i < 50; i++)
            AssertLocation(index, i, 4096L * i, 256);
    }

    [Fact]
    public void PutBatch_collapses_duplicates_last_wins()
    {
        var index = CreateIndex();
        var batch = new List<BlockLocation>
        {
            new() { BlockId = BlockId(1), Offset = 10, TotalBlockLength = 1 },
            new() { BlockId = BlockId(1), Offset = 20, TotalBlockLength = 2 }, // wins
            new() { BlockId = BlockId(2), Offset = 30, TotalBlockLength = 3 },
        };
        Ok(index.PutBatch(batch));
        Assert.Equal(2, index.Count);
        AssertLocation(index, 1, 20, 2);
        AssertLocation(index, 2, 30, 3);
    }

    [Fact]
    public void PutBatch_empty_is_a_noop_success()
    {
        var index = CreateIndex();
        Ok(index.PutBatch([]));
        Assert.Null(index.Root);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void TryGetLocation_implements_the_resolver_contract()
    {
        var index = CreateIndex();
        Ok(index.Put(BlockId(5), offset: 65536, length: 4096));

        Assert.True(index.TryGetLocation(BlockId(5), out var found));
        Assert.NotNull(found);
        Assert.Equal(65536, found!.Offset);
        Assert.Equal(4096, found.TotalBlockLength);
        Assert.Equal(BlockId(5), found.BlockId);

        Assert.False(index.TryGetLocation(BlockId(6), out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void A_mutation_leaves_the_previous_root_a_readable_snapshot()
    {
        var index = CreateIndex();
        Ok(index.Put(BlockId(1), 10, 20));
        var v1 = index.Root!;

        Ok(index.Put(BlockId(2), 30, 40));
        // The old snapshot still resolves only what it knew, through the tree.
        var onV1 = index.Tree.TryGet(v1, BlockId(2));
        Ok(onV1);
        Assert.False(onV1.Value.Found);
        var onV1Existing = index.Tree.TryGet(v1, BlockId(1));
        Ok(onV1Existing);
        Assert.True(onV1Existing.Value.Found);
    }

    [Fact]
    public void Rejects_a_wrong_width_blockid()
    {
        var index = CreateIndex();
        Assert.Throws<ArgumentException>(() => index.Put(new byte[15], 0, 0));
        Assert.Throws<ArgumentException>(() => index.Lookup(new byte[17]));
        Assert.Throws<ArgumentException>(() => index.Delete(new byte[0]));
    }

    [Fact]
    public void Rejects_negative_offset_or_length()
    {
        var index = CreateIndex();
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Put(BlockId(1), offset: -1, length: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Put(BlockId(1), offset: 10, length: -1));
    }

    // ----------------------- O(log n) lookup with upper levels cached ---------
    // Acceptance criterion (US-EMDB-71): "Lookup of any committed block is O(log n)
    // with upper levels cached." These tests prove it by counting the node store's
    // disk reads and cache hits per lookup on a committed (batch-inserted, then
    // flushed) multi-level index, read back through a FRESH cold-cache store so the
    // first lookup measures the true on-disk cost. A lookup issues exactly one
    // BTreeNodeStore.ReadVerified per level, each of which increments either
    // CacheHitCount (served from cache) or CacheMissCount (read from the block
    // layer) — so hits + misses is exactly the number of nodes the lookup touched.

    /// <summary>
    /// Batch-inserts <paramref name="count"/> blocks (the checkpoint insert path,
    /// spec Section 7) into a fresh index over the shared warm store, then flushes
    /// so every node block is durable — a committed population — and returns the
    /// index (its <see cref="BlockLocationIndex.Root"/> is the committed root).
    /// </summary>
    private BlockLocationIndex BuildCommittedIndex(int count, int maxLeafEntries = 4, int maxInternalKeys = 3)
    {
        var index = CreateIndex(maxLeafEntries, maxInternalKeys);
        var batch = new List<BlockLocation>(count);
        for (int i = 0; i < count; i++)
            batch.Add(new BlockLocation { BlockId = BlockId(i), Offset = 4096L * (i + 1), TotalBlockLength = 128 + i });
        Ok(index.PutBatch(batch));
        Ok(_manager.Flush()); // committed: every node block is now durable on disk
        Assert.NotNull(index.Root);
        return index;
    }

    /// <summary>
    /// Opens the committed <paramref name="root"/> through a FRESH node store with a
    /// cold cache and zeroed counters, so lookups measure true on-disk reads. No
    /// IBlockIdResolver: the offset-addressed index resolves every node by ChildOffset.
    /// </summary>
    private BlockLocationIndex OpenCold(BTreeRoot root, out BTreeNodeStore coldStore,
        int cacheCapacity = BTreeNodeStore.DefaultNodeCacheCapacity)
    {
        coldStore = new BTreeNodeStore(_manager, blockIdResolver: null, nodeCacheCapacity: cacheCapacity);
        return new BlockLocationIndex(coldStore, initialRoot: root);
    }

    [Fact]
    public void Lookup_touches_exactly_tree_height_nodes_for_any_committed_block()
    {
        // O(log n): a point lookup descends the tree ONCE, touching exactly one node
        // per level = TreeHeight nodes — never a function of the entry count. Holds
        // for present keys anywhere in key order AND for absent keys (which still
        // route down to a leaf). Each probe uses a fresh cold cache so hits + misses
        // is the raw node-access count for that single lookup.
        var index = BuildCommittedIndex(600);
        var root = index.Root!;
        Assert.True(root.Height >= 3, $"Need a multi-level tree; height was {root.Height}.");
        Assert.True(root.Height < index.Count, "Height must be far below the entry count (log n, not n).");

        int[] probes = { 0, 1, 150, 300, 599, 600, 100_000 }; // 600 and 100000 are absent
        foreach (var i in probes)
        {
            var cold = OpenCold(root, out var store); // fresh cold cache per probe
            var lookup = cold.Lookup(BlockId(i));
            Ok(lookup);
            long touched = store.CacheHitCount + store.CacheMissCount;
            Assert.Equal(root.Height, touched);
        }
    }

    [Fact]
    public void Cold_lookup_reads_exactly_tree_height_blocks_from_disk_not_n()
    {
        var index = BuildCommittedIndex(600);
        var root = index.Root!;
        Assert.True(root.Height >= 3, $"Need a multi-level tree; height was {root.Height}.");

        var cold = OpenCold(root, out var store);
        var lookup = cold.Lookup(BlockId(300));
        Ok(lookup);
        Assert.True(lookup.Value.Found);

        Assert.Equal(root.Height, store.CacheMissCount);       // one disk read per level
        Assert.Equal(0, store.CacheHitCount);                  // nothing was cached yet
        Assert.Equal(root.Height, store.HashComputationCount); // each read verified once
        // The whole point: reads scale with height, not the 600-entry population.
        Assert.True(store.CacheMissCount * 10 < index.Count,
            $"A lookup read {store.CacheMissCount} blocks for {index.Count} entries — must be O(log n), not O(n).");
    }

    [Fact]
    public void Repeated_lookup_serves_every_level_from_cache_without_re_reading_or_re_hashing()
    {
        var index = BuildCommittedIndex(600);
        var root = index.Root!;
        var cold = OpenCold(root, out var store);
        var key = BlockId(300);

        Ok(cold.Lookup(key));
        Assert.Equal(root.Height, store.CacheMissCount);
        Assert.Equal(root.Height, store.HashComputationCount);
        Assert.Equal(0, store.CacheHitCount);

        long missesAfterFirst = store.CacheMissCount;
        long hashesAfterFirst = store.HashComputationCount;

        Ok(cold.Lookup(key));
        // Second lookup: every node on the path — root, internals, leaf — came from
        // the cache, so no new disk reads and no re-hashing (verify-on-cache-load).
        Assert.Equal(missesAfterFirst, store.CacheMissCount);        // zero new disk reads
        Assert.Equal(hashesAfterFirst, store.HashComputationCount);  // zero re-hashing
        Assert.Equal(root.Height, store.CacheHitCount);             // Height cache hits
    }

    [Fact]
    public void Different_key_lookups_reuse_cached_upper_levels()
    {
        // "Upper levels cached": a lookup of a key in a DIFFERENT leaf still shares
        // the root (and any common internal ancestors) with the first lookup, so
        // those upper nodes are served from cache — the second lookup reads fewer
        // than Height blocks from disk yet registers at least one cache hit.
        var index = BuildCommittedIndex(600);
        var root = index.Root!;
        var cold = OpenCold(root, out var store);

        Ok(cold.Lookup(BlockId(0)));   // leftmost leaf, entirely cold
        Assert.Equal(root.Height, store.CacheMissCount);

        long missesBefore = store.CacheMissCount;
        long hitsBefore = store.CacheHitCount;
        Ok(cold.Lookup(BlockId(599))); // rightmost leaf: a disjoint path that still shares the root
        long newMisses = store.CacheMissCount - missesBefore;
        long newHits = store.CacheHitCount - hitsBefore;

        Assert.Equal(root.Height, newMisses + newHits);   // still exactly Height node accesses
        Assert.True(newHits >= 1, "The shared root/upper levels must be served from cache.");
        Assert.True(newMisses < root.Height, "Sharing an upper level means fewer than Height disk reads.");

        // Every further lookup keeps reusing the cached upper levels — the root is
        // never re-read from disk once it has been traversed.
        long hitsBeforeThird = store.CacheHitCount;
        Ok(cold.Lookup(BlockId(300)));
        Assert.True(store.CacheHitCount - hitsBeforeThird >= 1,
            "The root stays cached and is reused by every subsequent lookup.");
    }

    [Fact]
    public void Second_full_pass_over_committed_tree_reads_nothing_from_disk()
    {
        // With a cache large enough for the whole committed tree, a first pass warms
        // every node and a second pass over all committed blocks reads ZERO blocks
        // from disk — upper levels included — proving they remain cached. Sizing the
        // cache to the exact node count guarantees no eviction, so the first pass
        // reads each node from disk exactly once.
        const int n = 700;
        var index = BuildCommittedIndex(n);
        var root = index.Root!;
        long totalNodes = index.Tree.VerifyFullTree(root).NodesVerified;
        Assert.True(totalNodes > root.Height, "A multi-level tree has more nodes than its height.");

        var cold = OpenCold(root, out var store, cacheCapacity: (int)totalNodes + 16);

        for (int i = 0; i < n; i++)
        {
            var lookup = cold.Lookup(BlockId(i));
            Ok(lookup);
            Assert.True(lookup.Value.Found, $"Committed block {i} should resolve.");
        }
        // Across the whole first pass each distinct node was read from disk once.
        Assert.Equal(totalNodes, store.CacheMissCount);

        long missesAfterWarm = store.CacheMissCount;
        long hitsAfterWarm = store.CacheHitCount;
        for (int i = 0; i < n; i++)
            Ok(cold.Lookup(BlockId(i)));

        Assert.Equal(missesAfterWarm, store.CacheMissCount); // second pass: not one disk read
        Assert.True(store.CacheHitCount - hitsAfterWarm > 0, "The second pass must be served entirely from cache.");
    }
}
