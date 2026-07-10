using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="DateIndex"/> — the Date secondary index (US-EMDB-93,
/// EmailDB_FileFormat_Spec.md Section 7, docs/BTree_Index.md Section 7,
/// IndexKind 2): composite <c>DateTicks ‖ BlockId</c> keys with empty values,
/// maintained on add/remove, range-queried without scanning pages, and
/// registered in the Checkpoint's generic secondary-index table under
/// IndexKind 2. The node store is given the offset map as its BlockId resolver
/// because a Date tree is BlockId-addressed (spec Section 7).
/// </summary>
public class DateIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-date-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;
    private readonly List<string> _tempFiles = new();

    public DateIndexTests()
    {
        var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        _manager = new BlockManager(stream, offsetMap: _offsetMap, firstBlockOffset: 0, ownsStream: true);
        _store = new BTreeNodeStore(_manager, _offsetMap);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
        foreach (var f in _tempFiles)
            if (File.Exists(f))
                File.Delete(f);
    }

    // ------------------------------------------------------------- Helpers

    /// <summary>Tiny fan-out so a handful of entries forces internal levels and cross-leaf scans.</summary>
    private DateIndex CreateIndex(int maxLeafEntries = 4, int maxInternalKeys = 3) =>
        new(_store, maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

    /// <summary>A 16-byte BlockId whose lexicographic order equals the numeric order of <paramref name="i"/>.</summary>
    private static byte[] BlockId(int i)
    {
        var id = new byte[DateIndex.BlockIdSize];
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), i);
        return id;
    }

    private static void Ok(Result result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
    private static void Ok<T>(Result<T> result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    // ------------------------------------------------------------- Key layout

    [Fact]
    public void CompositeKey_IsDateTicksBigEndianThenBlockId_24Bytes()
    {
        Assert.Equal(24, DateIndex.CompositeKeySize);

        Span<byte> key = stackalloc byte[DateIndex.CompositeKeySize];
        var blockId = BlockId(7);
        DateIndex.EncodeKey(0x0102030405060708, blockId, key);

        // Ticks are big-endian in the first 8 bytes...
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, key[..8].ToArray());
        // ...followed by the 16-byte BlockId.
        Assert.Equal(blockId, key.Slice(8, 16).ToArray());

        DateIndex.DecodeKey(key, out long ticks, out byte[] roundTrip);
        Assert.Equal(0x0102030405060708, ticks);
        Assert.Equal(blockId, roundTrip);
    }

    // ------------------------------------------------------ Range query (AC 1)

    [Fact]
    public void RangeQuery_ReturnsExactlyTheEmailsInRange_InAscendingOrder()
    {
        var index = CreateIndex();
        // Insert timestamps 10..100 in scrambled order across several leaves.
        int[] ticks = { 50, 10, 90, 30, 70, 20, 100, 40, 80, 60 };
        foreach (var t in ticks)
            Ok(index.Add(t, BlockId(t)));

        var result = index.RangeQuery(30, 70);
        Ok(result);

        var got = result.Value.Select(e => e.DateTicks).ToArray();
        Assert.Equal(new long[] { 30, 40, 50, 60, 70 }, got); // inclusive both ends, sorted
        // Each entry carries the matching BlockId.
        foreach (var e in result.Value)
            Assert.Equal(BlockId((int)e.DateTicks), e.BlockId);
    }

    [Fact]
    public void RangeQuery_EmptyIndex_ReturnsEmptyList()
    {
        var index = CreateIndex();
        var result = index.RangeQuery(0, long.MaxValue);
        Ok(result);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void RangeQuery_NoEntriesInRange_ReturnsEmptyList()
    {
        var index = CreateIndex();
        Ok(index.Add(10, BlockId(10)));
        Ok(index.Add(20, BlockId(20)));

        var result = index.RangeQuery(100, 200);
        Ok(result);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void RangeQuery_InvertedRange_ReturnsFailure()
    {
        var index = CreateIndex();
        Ok(index.Add(10, BlockId(10)));

        var result = index.RangeQuery(100, 10);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void RangeQuery_UpperBoundInclusiveAtMaxTicks_IncludesEverythingFromStart()
    {
        var index = CreateIndex();
        for (int t = 1; t <= 20; t++)
            Ok(index.Add(t, BlockId(t)));

        var result = index.RangeQuery(15, long.MaxValue);
        Ok(result);
        Assert.Equal(new long[] { 15, 16, 17, 18, 19, 20 }, result.Value.Select(e => e.DateTicks).ToArray());
    }

    [Fact]
    public void RangeQuery_BoundariesAreInclusive_AndImmediateNeighboursAreExcluded()
    {
        // A contiguous run so the entries at from-1 and to+1 sit RIGHT NEXT to
        // the boundary — the strictest exclusion check: the query must include
        // both endpoints exactly and stop one tick short on either side.
        var index = CreateIndex();
        for (int t = 1; t <= 10; t++)
            Ok(index.Add(t, BlockId(t)));

        var result = index.RangeQuery(4, 7);
        Ok(result);

        var got = result.Value.Select(e => e.DateTicks).ToArray();
        // Strictly within [4,7]: both bounds included, neighbours 3 and 8 excluded.
        Assert.Equal(new long[] { 4, 5, 6, 7 }, got);
        Assert.DoesNotContain(3L, got); // from - 1
        Assert.DoesNotContain(8L, got); // to + 1
    }

    [Fact]
    public void RangeQuery_ReadsFarFewerBlocksThanAFullScan_OverTheSameTree()
    {
        // A multi-level tree spanning many leaves; a page scan would read them all.
        const int total = 200;
        var (root, map, mgr, height) = BuildIndependentTree(total);
        try
        {
            Assert.True(height >= 3); // genuinely multi-level

            // Each query runs against its OWN fresh, cold-cache store over the same
            // block file, so CacheMissCount == node blocks actually read from disk.
            long ReadsFor(long from, long to, out int resultCount)
            {
                var cold = new BTreeNodeStore(mgr, map);
                var index = new DateIndex(cold, initialRoot: root, maxLeafEntries: 4, maxInternalKeys: 3);
                var result = index.RangeQuery(from, to);
                Ok(result);
                resultCount = result.Value.Count;
                return cold.CacheMissCount;
            }

            long narrowReads = ReadsFor(100, 108, out int narrowCount); // 9 in-range
            long fullReads = ReadsFor(1, total, out int fullCount);     // whole tree

            Assert.Equal(9, narrowCount);
            Assert.Equal(total, fullCount);
            // The narrow query pages in only a small fraction of the blocks the
            // page-scan alternative touches — the ~191 out-of-range entries are
            // never read.
            Assert.True(narrowReads * 3 < fullReads,
                $"narrow range read {narrowReads} blocks; a full scan read {fullReads} — expected a small fraction, not a page scan.");
        }
        finally { mgr.Dispose(); }
    }

    [Fact]
    public void RangeQuery_ReadCost_ScalesWithResultAndHeight_NotWithTreeSize()
    {
        // The definitive "no page scan" evidence: hold the query window fixed at
        // 9 entries and grow the tree 16x. If the query scanned pages, reads
        // would grow ~16x; because it seeks, reads grow only with the tree's
        // HEIGHT (log n), independent of the out-of-range data volume.
        var small = BuildIndependentTree(64);
        var big = BuildIndependentTree(1024);
        try
        {
            Assert.True(big.Height > small.Height); // 16x data -> deeper tree

            long WindowReads((BTreeRoot Root, RuntimeBlockOffsetMap Map, BlockManager Mgr, int Height) tree, out int count)
            {
                var cold = new BTreeNodeStore(tree.Mgr, tree.Map);
                var index = new DateIndex(cold, initialRoot: tree.Root, maxLeafEntries: 4, maxInternalKeys: 3);
                var result = index.RangeQuery(20, 28); // same 9-entry window in both
                Ok(result);
                count = result.Value.Count;
                return cold.CacheMissCount;
            }

            long smallReads = WindowReads(small, out int smallCount);
            long bigReads = WindowReads(big, out int bigCount);

            Assert.Equal(9, smallCount);
            Assert.Equal(9, bigCount); // identical result set, 16x the surrounding data
            // Reads grow only by roughly the extra depth (a couple of nodes per
            // added level), NOT by the 16x factor a page scan would incur.
            long allowedGrowth = 2L * (big.Height - small.Height) + 2;
            Assert.True(bigReads <= smallReads + allowedGrowth,
                $"same 9-entry window read {smallReads} blocks in the small tree and {bigReads} in the 16x-larger tree; " +
                $"expected growth <= {allowedGrowth} (log-depth only), not proportional to tree size.");
        }
        finally { small.Mgr.Dispose(); big.Mgr.Dispose(); }
    }

    /// <summary>
    /// Builds a DateIndex of <paramref name="n"/> contiguous timestamps (1..n) in
    /// its OWN in-memory block store with tiny fan-out, flushes it durable, and
    /// returns the committed root plus the store handles so a cold-cache reader
    /// can count the blocks a query reads. Caller disposes the returned manager.
    /// </summary>
    private (BTreeRoot Root, RuntimeBlockOffsetMap Map, BlockManager Mgr, int Height) BuildIndependentTree(int n)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-date-scan-{Guid.NewGuid():N}.emdb");
        _tempFiles.Add(path);
        var map = new RuntimeBlockOffsetMap();
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var mgr = new BlockManager(stream, offsetMap: map, firstBlockOffset: 0, ownsStream: true);
        var index = new DateIndex(new BTreeNodeStore(mgr, map), maxLeafEntries: 4, maxInternalKeys: 3);
        for (int t = 1; t <= n; t++)
            Ok(index.Add(t, BlockId(t)));
        Ok(mgr.Flush());
        return (index.Root!, map, mgr, index.Root!.Height);
    }

    // ------------------------------------------- Duplicate timestamps (AC 2)

    [Fact]
    public void DuplicateTimestamps_AllStoredAndReturned_DistinguishedByBlockId()
    {
        var index = CreateIndex();
        // Five different emails, all at the same timestamp.
        var ids = new[] { BlockId(1), BlockId(2), BlockId(3), BlockId(4), BlockId(5) };
        foreach (var id in ids)
            Ok(index.Add(1000, id));

        Assert.Equal(5, index.Count);

        var result = index.RangeQuery(1000, 1000);
        Ok(result);
        Assert.Equal(5, result.Value.Count);
        Assert.All(result.Value, e => Assert.Equal(1000, e.DateTicks));
        // The BlockId suffix distinguishes them and orders them.
        Assert.Equal(ids, result.Value.Select(e => e.BlockId).ToArray());
    }

    [Fact]
    public void DuplicateTimestamps_OrderedByBlockIdSuffix_NotByInsertionOrder()
    {
        var index = CreateIndex();
        // All at the SAME timestamp, inserted in a scrambled order so the
        // returned order can only match the BlockId suffix if the tree really
        // sorts by it — insertion order would give a different sequence.
        int[] insertionOrder = { 4, 1, 5, 2, 3 };
        foreach (var i in insertionOrder)
            Ok(index.Add(2000, BlockId(i)));

        Assert.Equal(5, index.Count); // no collision: all five distinct keys

        var result = index.RangeQuery(2000, 2000);
        Ok(result);
        // Deterministic ascending order BY BlockId suffix, independent of the
        // scrambled insertion order above.
        Assert.Equal(
            new[] { BlockId(1), BlockId(2), BlockId(3), BlockId(4), BlockId(5) },
            result.Value.Select(e => e.BlockId).ToArray());
        Assert.NotEqual(
            insertionOrder.Select(BlockId).ToArray(),
            result.Value.Select(e => e.BlockId).ToArray());
    }

    [Fact]
    public void DuplicateTimestamps_SpanningLeafSplits_AllRetainedAndOrderedByBlockId()
    {
        var index = CreateIndex(); // tiny fan-out (maxLeafEntries: 4)
        // Far more duplicates at one timestamp than a single leaf holds, added
        // in descending order so the suffix ordering is exercised across the
        // internal levels the splits create — proving the BlockId suffix keeps
        // every duplicate a distinct, correctly ordered key even after splits.
        const int count = 20;
        for (int i = count; i >= 1; i--)
            Ok(index.Add(7000, BlockId(i)));

        Assert.Equal(count, index.Count);          // no collisions across splits
        Assert.True(index.Root!.Height >= 2);      // genuinely multi-level

        var result = index.RangeQuery(7000, 7000);
        Ok(result);
        Assert.Equal(count, result.Value.Count);
        Assert.All(result.Value, e => Assert.Equal(7000, e.DateTicks));
        Assert.Equal(
            Enumerable.Range(1, count).Select(BlockId).ToArray(),
            result.Value.Select(e => e.BlockId).ToArray());
    }

    [Fact]
    public void DuplicateTimestamps_InterleavedWithNeighbours_StayGroupedAndBounded()
    {
        // Duplicates at a shared tick must sort strictly between the ticks on
        // either side — the suffix never lets a duplicate leak past its tick.
        var index = CreateIndex();
        Ok(index.Add(90, BlockId(99)));            // one tick below
        Ok(index.Add(110, BlockId(0)));            // one tick above
        foreach (var i in new[] { 3, 8, 1, 5 })    // scrambled duplicates at 100
            Ok(index.Add(100, BlockId(i)));

        var at100 = index.RangeQuery(100, 100);
        Ok(at100);
        Assert.Equal(
            new[] { BlockId(1), BlockId(3), BlockId(5), BlockId(8) },
            at100.Value.Select(e => e.BlockId).ToArray());

        // The full ascending sweep places the whole duplicate group contiguously
        // between the neighbouring ticks, in (ticks, blockId) order.
        var all = index.RangeQuery(0, long.MaxValue);
        Ok(all);
        Assert.Equal(
            new long[] { 90, 100, 100, 100, 100, 110 },
            all.Value.Select(e => e.DateTicks).ToArray());
    }

    [Fact]
    public void Remove_OneEmailAtSharedTimestamp_LeavesTheOthers()
    {
        var index = CreateIndex();
        Ok(index.Add(1000, BlockId(1)));
        Ok(index.Add(1000, BlockId(2)));
        Ok(index.Add(1000, BlockId(3)));

        var removed = index.Remove(1000, BlockId(2));
        Ok(removed);
        Assert.True(removed.Value);
        Assert.Equal(2, index.Count);

        var result = index.RangeQuery(1000, 1000);
        Ok(result);
        Assert.Equal(new[] { BlockId(1), BlockId(3) }, result.Value.Select(e => e.BlockId).ToArray());
    }

    // ------------------------------------------- Maintained on add/delete (AC 3)

    [Fact]
    public void Add_IsIdempotent_ForSameTicksAndBlockId()
    {
        var index = CreateIndex();
        Ok(index.Add(5, BlockId(5)));
        Ok(index.Add(5, BlockId(5)));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Remove_AbsentEntry_IsSuccessfulNoOp()
    {
        var index = CreateIndex();
        Ok(index.Add(5, BlockId(5)));

        var removed = index.Remove(9, BlockId(9));
        Ok(removed);
        Assert.False(removed.Value);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void AddThenRemove_AcrossManyLeaves_RangeReflectsLiveSet()
    {
        var index = CreateIndex(); // tiny fan-out -> multi-level tree
        for (int t = 1; t <= 50; t++)
            Ok(index.Add(t, BlockId(t)));
        Assert.True(index.Tree.MaxLeafEntries < 50); // forced splits
        Assert.True(index.Root!.Height >= 2);

        // Delete every even timestamp.
        for (int t = 2; t <= 50; t += 2)
            Assert.True(index.Remove(t, BlockId(t)).Value);

        var result = index.RangeQuery(1, 50);
        Ok(result);
        var expected = Enumerable.Range(1, 50).Where(t => t % 2 == 1).Select(t => (long)t).ToArray();
        Assert.Equal(expected, result.Value.Select(e => e.DateTicks).ToArray());
    }

    // ---------------------------------- Checkpoint registration & recovery (AC 3)

    [Fact]
    public void ToCheckpointSecondaryIndex_RegistersRootUnderIndexKind2()
    {
        var index = CreateIndex();
        Ok(index.Add(10, BlockId(10)));
        Ok(index.Add(20, BlockId(20)));

        var entry = index.ToCheckpointSecondaryIndex(rootOffset: 4096);
        Assert.Equal(BTreeIndexKind.Date, entry.IndexKind);
        Assert.Equal((ushort)2, (ushort)entry.IndexKind);
        Assert.Equal(index.Root!.RootRef.Reference, entry.Pointer.BlockId);
        Assert.Equal(4096, entry.Pointer.Offset);
    }

    [Fact]
    public void CheckpointRegistration_SurvivesSerializationRoundTrip()
    {
        var index = CreateIndex();
        for (int t = 1; t <= 12; t++)
            Ok(index.Add(t, BlockId(t)));

        var dateEntry = index.ToCheckpointSecondaryIndex(rootOffset: 8192);
        var checkpoint = new Checkpoint
        {
            FormatVersion = 3,
            CheckpointSequence = 1,
            FileId = new byte[16],
            PreviousCheckpoint = CheckpointRootPointer.None,
            FolderTreeRoot = CheckpointRootPointer.None,
            PrimaryIndexRoot = CheckpointRootPointer.None,
            LocationIndexRoot = CheckpointRootPointer.None,
            MetadataRoot = CheckpointRootPointer.None,
            KeyStoreRoot = CheckpointRootPointer.None,
            SecondaryIndexes = new[] { dateEntry },
            LiveBlockCount = 12,
            LiveByteCount = 0,
            DeadByteCount = 0,
        };

        var payload = CheckpointSerializer.Serialize(checkpoint);
        var reread = CheckpointSerializer.Deserialize(payload);
        Ok(reread);

        var secondaries = reread.Value.SecondaryIndexes;
        Assert.Single(secondaries);
        Assert.Equal(BTreeIndexKind.Date, secondaries[0].IndexKind);
        Assert.Equal(dateEntry.Pointer.BlockId, secondaries[0].Pointer.BlockId);
        Assert.Equal(8192, secondaries[0].Pointer.Offset);
    }

    [Fact]
    public void Recovery_ReopeningIndexAtRegisteredRoot_ServesSameRangeQuery()
    {
        // Build and "commit": mutate, then capture the committed root the
        // Checkpoint would persist.
        var writer = CreateIndex();
        for (int t = 1; t <= 30; t++)
            Ok(writer.Add(t, BlockId(t)));
        var committedRoot = writer.Root;
        Ok(_manager.Flush()); // make the appended node blocks durable

        var before = writer.RangeQuery(10, 20);
        Ok(before);

        // "Reopen": a fresh DateIndex over the SAME store, handed only the
        // committed root recovered from the Checkpoint — no rebuild, no scan.
        var reopened = new DateIndex(_store, initialRoot: committedRoot,
            maxLeafEntries: 4, maxInternalKeys: 3);

        var after = reopened.RangeQuery(10, 20);
        Ok(after);
        Assert.Equal(
            before.Value.Select(e => e.DateTicks).ToArray(),
            after.Value.Select(e => e.DateTicks).ToArray());
        Assert.Equal(new long[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
            after.Value.Select(e => e.DateTicks).ToArray());
    }

    [Fact]
    public void Recovery_FullCloseAndReopenFromDisk_ReflectsCommittedAddsAndDeletes()
    {
        // The end-to-end recovery flow for "maintained on add and delete AND
        // recovered via Checkpoint": build a multi-level index with BOTH adds and
        // deletes, commit it (flush + a real serialized Checkpoint), then CLOSE the
        // file entirely and REOPEN a brand-new BlockManager/store over the same
        // bytes. The ONLY thing carried across the close is what the Checkpoint
        // persists — the serialized secondary-index entry (root BlockId + offset
        // hint) plus the committed entry count. The reopened index is rebuilt
        // solely from those bytes and must serve exactly the committed live set.
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-date-recover-{Guid.NewGuid():N}.emdb");
        _tempFiles.Add(path);

        byte[] checkpointPayload;
        long committedCount;
        long[] committedTicks;
        int[] deletedTicks = { 2, 8, 14, 20, 26, 32, 38, 44, 50 }; // some evens, spread across leaves

        // ---- Session 1: build, mutate (add + delete), commit, then close ----
        {
            var map = new RuntimeBlockOffsetMap();
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            var mgr = new BlockManager(stream, offsetMap: map, firstBlockOffset: 0, ownsStream: true);
            var index = new DateIndex(new BTreeNodeStore(mgr, map), maxLeafEntries: 4, maxInternalKeys: 3);

            for (int t = 1; t <= 50; t++)
                Ok(index.Add(t, BlockId(t)));
            foreach (var t in deletedTicks)
                Assert.True(index.Remove(t, BlockId(t)).Value); // delete really removed the entry
            Assert.True(index.Root!.Height >= 2);               // genuinely multi-level COW tree

            Ok(mgr.Flush()); // make every appended node block durable BEFORE recording the Checkpoint

            // The Checkpoint records the current root by its BlockId + the file offset
            // it was appended at (the resolver knows where the live root block lives).
            Assert.True(map.TryGetLocation(index.Root.RootRef.Reference, out var rootLoc) && rootLoc is not null);
            var dateEntry = index.ToCheckpointSecondaryIndex(rootLoc!.Offset);

            var checkpoint = new Checkpoint
            {
                FormatVersion = 3,
                CheckpointSequence = 1,
                FileId = new byte[16],
                PreviousCheckpoint = CheckpointRootPointer.None,
                FolderTreeRoot = CheckpointRootPointer.None,
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = CheckpointRootPointer.None,
                MetadataRoot = CheckpointRootPointer.None,
                KeyStoreRoot = CheckpointRootPointer.None,
                SecondaryIndexes = new[] { dateEntry },
                LiveBlockCount = index.Count,
                LiveByteCount = 0,
                DeadByteCount = 0,
            };
            checkpointPayload = CheckpointSerializer.Serialize(checkpoint);

            committedCount = index.Count;
            var live = index.RangeQuery(0, long.MaxValue);
            Ok(live);
            committedTicks = live.Value.Select(e => e.DateTicks).ToArray();

            mgr.Dispose(); // CLOSE the file — nothing in memory survives except checkpointPayload
        }

        // The committed live set is 1..50 minus the deleted ticks.
        var expectedTicks = Enumerable.Range(1, 50)
            .Where(t => !deletedTicks.Contains(t)).Select(t => (long)t).ToArray();
        Assert.Equal(expectedTicks, committedTicks);
        Assert.Equal(expectedTicks.Length, committedCount);

        // ---- Session 2: reopen from disk, recover the root via the Checkpoint ----
        var reopenStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var reopenMap = new RuntimeBlockOffsetMap();
        var reopenMgr = new BlockManager(reopenStream, offsetMap: reopenMap, firstBlockOffset: 0, ownsStream: true);
        try
        {
            // A fresh map is empty on open; a forward scan of the reopened file
            // repopulates every block's location so BlockId-addressed nodes resolve
            // (spec Section 7 resolution chain).
            Ok(reopenMgr.ScanForward(reopenMap));
            var reopenStore = new BTreeNodeStore(reopenMgr, reopenMap);

            // Recover the root pointer STRICTLY from the serialized Checkpoint bytes.
            var reread = CheckpointSerializer.Deserialize(checkpointPayload);
            Ok(reread);
            var secondary = reread.Value.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Date);

            // Rebuild the BTreeRoot from what the Checkpoint persisted: the root
            // BlockId + verified offset hint. Reading the root node off disk recovers
            // its Merkle hash and the tree height (leftmost-spine walk) — the same
            // O(height) reconstruction CleanOpener does for the location index, with
            // no full tree scan.
            var recoveredRoot = ReconstructBlockIdRootFromDisk(
                reopenMgr, reopenMap,
                secondary.Pointer.BlockId, secondary.Pointer.Offset, committedCount);
            Ok(recoveredRoot);

            var reopened = new DateIndex(reopenStore, initialRoot: recoveredRoot.Value,
                maxLeafEntries: 4, maxInternalKeys: 3);

            // The recovered index sees EXACTLY the committed state: every survivor
            // present in order, and every deleted entry absent — no rebuild, no scan.
            Assert.Equal(committedCount, reopened.Count);
            var recoveredQuery = reopened.RangeQuery(0, long.MaxValue);
            Ok(recoveredQuery);
            Assert.Equal(expectedTicks, recoveredQuery.Value.Select(e => e.DateTicks).ToArray());
            foreach (var t in deletedTicks)
            {
                var gone = reopened.RangeQuery(t, t);
                Ok(gone);
                Assert.Empty(gone.Value); // deletes survived the close/reopen
            }
            // A survivor's BlockId round-trips intact through recovery.
            var window = reopened.RangeQuery(10, 13);
            Ok(window);
            Assert.Equal(
                new[] { BlockId(10), BlockId(11), BlockId(12), BlockId(13) },
                window.Value.Select(e => e.BlockId).ToArray());
        }
        finally { reopenMgr.Dispose(); }
    }

    /// <summary>
    /// Rebuilds a BlockId-addressed <see cref="BTreeRoot"/> from only what the
    /// Checkpoint persists — the root BlockId and its verified offset hint. Reads
    /// the root node at the offset to recover its content hash, then descends the
    /// leftmost spine (resolving each child BlockId through the reopened map) to
    /// recover the tree height. Mirrors <c>CleanOpener.ReconstructLocationIndex</c>
    /// for a BlockId-addressed index; O(height) targeted reads, never a scan.
    /// </summary>
    private static Result<BTreeRoot> ReconstructBlockIdRootFromDisk(
        BlockManager mgr, IBlockIdResolver resolver, byte[] rootBlockId, long rootOffset, long entryCount)
    {
        byte[]? rootHash = null;
        long offset = rootOffset;
        int height = 0;
        while (true)
        {
            var block = mgr.ReadDecompressed(offset);
            if (block.IsFailure)
                return Result<BTreeRoot>.Failure($"reading node at offset {offset}: {block.Error}");
            var payload = block.Value.Payload;

            var header = EmailDB.Format.V3.BTreeNodeSerializer.DeserializeHeader(payload);
            if (header.IsFailure)
                return Result<BTreeRoot>.Failure($"parsing node header at offset {offset}: {header.Error}");

            rootHash ??= EmailDB.Format.V3.BTreeNodeSerializer.ComputeNodeContentHash(payload);
            height++;

            if (header.Value.NodeKind == BTreeNodeKind.Leaf)
                break;

            var node = EmailDB.Format.V3.BTreeNodeSerializer.DeserializeInternal(payload);
            if (node.IsFailure)
                return Result<BTreeRoot>.Failure($"parsing internal node at offset {offset}: {node.Error}");

            var childRef = BTreeNodeRef.FromChildRecord(BTreeChildAddressing.BlockId, node.Value.Children[0]);
            if (childRef.IsFailure)
                return Result<BTreeRoot>.Failure(childRef.Error);
            if (!resolver.TryGetLocation(childRef.Value.Reference, out var loc) || loc is null)
                return Result<BTreeRoot>.Failure(
                    $"child BlockId {Convert.ToHexString(childRef.Value.Reference)} did not resolve during recovery.");
            offset = loc.Offset;
        }

        return Result<BTreeRoot>.Success(new BTreeRoot
        {
            RootRef = new BTreeNodeRef
            {
                Addressing = BTreeChildAddressing.BlockId,
                Reference = rootBlockId,
                NodeHash = rootHash!,
            },
            Height = height,
            EntryCount = entryCount,
        });
    }

    // ------------------------------------------- Pre-filter composition (AC 4)

    [Fact]
    public void RangeQuery_ComposesAsPreFilter_ByHandingBlockIdsToNextPhase()
    {
        var index = CreateIndex();
        for (int t = 1; t <= 40; t++)
            Ok(index.Add(t, BlockId(t)));

        // Date phase: narrow the candidate set to a time window.
        var candidates = index.RangeQuery(15, 25);
        Ok(candidates);
        var candidateIds = candidates.Value.Select(e => e.BlockId).ToHashSet(ByteArrayComparer.Instance);

        // A subsequent phase (simulated) intersects its own hits with the date
        // candidates — only BlockIds passing BOTH survive.
        var otherPhaseHits = new[] { BlockId(10), BlockId(18), BlockId(22), BlockId(35) };
        var combined = otherPhaseHits.Where(id => candidateIds.Contains(id)).ToArray();

        Assert.Equal(new[] { BlockId(18), BlockId(22) }, combined);
    }

    [Fact]
    public void RangeQuery_AsPreFilter_NarrowsCandidatesThenConfirmingPhaseRefinesToExactResult()
    {
        // The Query-planning contract (docs/Search.md: "date-bounded? -> Phase 3
        // narrows first"; "other phases narrow candidates, page records confirm"):
        // the date index runs FIRST as a pre-filter, then a later phase confirms
        // only the survivors. This proves BOTH properties the AC requires:
        // (1) the date phase genuinely NARROWS the candidate set, and
        // (2) composing it with another phase yields EXACTLY the right result -
        //     entries that satisfy the later phase but fall outside the window are
        //     excluded BECAUSE the date pre-filter already dropped them.
        var index = CreateIndex();
        for (int t = 1; t <= 100; t++)
            Ok(index.Add(t, BlockId(t)));

        // Phase 3 (date) narrows the whole 100-email index to a time window.
        var candidates = index.RangeQuery(40, 60);
        Ok(candidates);
        var candidateIds = candidates.Value.Select(e => e.BlockId).ToHashSet(ByteArrayComparer.Instance);

        // It genuinely narrows: 21 survivors, strictly fewer than the 100 indexed.
        Assert.Equal(21, candidateIds.Count);
        Assert.True(candidateIds.Count < index.Count, "the date pre-filter must reduce the candidate set");

        // A confirming phase (e.g. a Tier-1 page-record predicate) runs ONLY over
        // the survivors and accepts multiples of 5.
        bool ConfirmPredicate(int t) => t % 5 == 0;
        var confirmed = candidateIds
            .Select(BlockIndexOf)
            .Where(ConfirmPredicate)
            .OrderBy(t => t)
            .ToArray();

        Assert.Equal(new[] { 40, 45, 50, 55, 60 }, confirmed);
        // 5,10,...,35 and 65,...,100 all satisfy the predicate too, yet are absent:
        // they never entered the candidate set, so this was a pre-filter, not a scan.
        Assert.DoesNotContain(35, confirmed);
        Assert.DoesNotContain(65, confirmed);
    }

    [Fact]
    public void RangeQuery_AsPreFilter_DisjointFromNextPhase_YieldsEmptyResult()
    {
        // Correctness of the composition at its boundary: when the date window and
        // the next phase's hits do not overlap, the combined result is exactly
        // empty - the pre-filter eliminates the entire candidate set the other
        // phase would have matched.
        var index = CreateIndex();
        for (int t = 1; t <= 40; t++)
            Ok(index.Add(t, BlockId(t)));

        var candidateIds = index.RangeQuery(1, 10).Value
            .Select(e => e.BlockId).ToHashSet(ByteArrayComparer.Instance);

        // Every one of the other phase's hits is outside the [1,10] window.
        var otherPhaseHits = new[] { BlockId(11), BlockId(25), BlockId(40) };
        var combined = otherPhaseHits.Where(id => candidateIds.Contains(id)).ToArray();

        Assert.Empty(combined);
    }

    /// <summary>Inverse of <see cref="BlockId"/>: recovers the ordinal <c>i</c> from a BlockId.</summary>
    private static int BlockIndexOf(byte[] blockId) =>
        BinaryPrimitives.ReadInt32BigEndian(blockId.AsSpan(12));

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
