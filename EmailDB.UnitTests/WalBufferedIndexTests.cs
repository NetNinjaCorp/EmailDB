using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="WalBufferedIndex"/>, the WAL-buffered batch flush for
/// one B+-tree index (US-EMDB-70-6, docs/BTree_Index.md Sections 4, 6;
/// EmailDB_FileFormat_Spec.md Section 6): count/time/explicit flush triggers,
/// buffered entries sorted by key and applied in one COW pass, per-index
/// monotonic IndexRoot.Sequence, read-your-writes over the buffer, and the
/// harmless-failure contract — a failed node, fsync, or IndexRoot write leaves
/// the previous root authoritative with the batch retained and only orphan
/// nodes on disk.
/// </summary>
public class WalBufferedIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-walbuffer-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly FaultInjectingFileStream _stream;
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;
    private readonly CowBTree _tree;

    public WalBufferedIndexTests()
    {
        _stream = new FaultInjectingFileStream(_path);
        _manager = new BlockManager(_stream, offsetMap: _offsetMap, firstBlockOffset: 0, ownsStream: true);
        _store = new BTreeNodeStore(_manager, _offsetMap);
        // PrimaryEmail-shaped (32-byte keys, 16-byte values) with tiny fan-out
        // so a handful of entries forces splits.
        _tree = new CowBTree(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 4, maxInternalKeys: 3);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // ------------------------------------------------------------- Helpers

    /// <summary>Records every IndexRoot the flusher persisted, and can be told to fail on demand.</summary>
    private sealed class RecordingIndexRootStore : IIndexRootStore
    {
        private readonly BlockManagerIndexRootStore _inner;
        public RecordingIndexRootStore(BlockManager manager) => _inner = new BlockManagerIndexRootStore(manager);

        public List<IndexRoot> Written { get; } = [];
        public bool FailNext { get; set; }

        public Result<BlockLocation> WriteIndexRoot(IndexRoot indexRoot)
        {
            if (FailNext)
                return Result<BlockLocation>.Failure("injected IndexRoot write failure");
            var result = _inner.WriteIndexRoot(indexRoot);
            if (result.IsSuccess)
                Written.Add(indexRoot);
            return result;
        }
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock the test advances manually.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private WalBufferedIndex CreateIndex(
        RecordingIndexRootStore rootStore,
        int? countThreshold = null,
        TimeSpan? flushInterval = null,
        TimeProvider? timeProvider = null) =>
        new(_tree, rootStore, _manager.Flush,
            countThreshold: countThreshold, flushInterval: flushInterval, timeProvider: timeProvider);

    private RecordingIndexRootStore NewRootStore() => new(_manager);

    private static byte[] Key(int i)
    {
        var key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28), i);
        return key;
    }

    private static byte[] Value(int i)
    {
        var value = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(12), i);
        return value;
    }

    private static void Ok(Result result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    private static void Ok<T>(Result<T> result) => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);

    private void AssertFound(WalBufferedIndex index, int i, int expectedValueSeed)
    {
        var result = index.TryGet(Key(i));
        Ok(result);
        Assert.True(result.Value.Found, $"Key {i} should be present.");
        Assert.Equal(Value(expectedValueSeed), result.Value.Value);
    }

    private void AssertAbsent(WalBufferedIndex index, int i)
    {
        var result = index.TryGet(Key(i));
        Ok(result);
        Assert.False(result.Value.Found, $"Key {i} should be absent.");
    }

    /// <summary>Reads back every key of a committed tree version in scan order.</summary>
    private List<int> ScanKeys(BTreeRoot? root)
    {
        var scan = _tree.Scan(root);
        var keys = new List<int>();
        while (true)
        {
            var step = scan.MoveNext();
            Ok(step);
            if (!step.Value)
                break;
            keys.Add(BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(28)));
        }
        return keys;
    }

    // ----------------------------------------------------- Trigger: count

    [Fact]
    public void CountThreshold_FlushesAutomatically_WhenBufferFills()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 3);

        Ok(index.Upsert(Key(1), Value(1)));
        Ok(index.Upsert(Key(2), Value(2)));
        // Two entries: below threshold, nothing committed yet.
        Assert.Null(index.CommittedRoot);
        Assert.Equal(2, index.PendingCount);
        Assert.Empty(rootStore.Written);

        // Third entry hits the count threshold and flushes.
        Ok(index.Upsert(Key(3), Value(3)));
        Assert.Equal(0, index.PendingCount);
        Assert.NotNull(index.CommittedRoot);
        Assert.Equal(3, index.CommittedRoot!.EntryCount);
        Assert.Single(rootStore.Written);
        for (int i = 1; i <= 3; i++)
            AssertFound(index, i, i);
    }

    // ------------------------------------------------------ Trigger: time

    [Fact]
    public void TimeThreshold_FlushesOnFlushIfDue_AfterIntervalElapses()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore,
            countThreshold: 1000, flushInterval: TimeSpan.FromSeconds(5), timeProvider: clock);

        Ok(index.Upsert(Key(1), Value(1)));
        // Count threshold not reached; time not elapsed -> still buffered.
        Assert.Equal(1, index.PendingCount);
        Assert.False(index.IsFlushDue());

        // Before the interval: FlushIfDue is a no-op.
        clock.Advance(TimeSpan.FromSeconds(4));
        Ok(index.FlushIfDue());
        Assert.Equal(1, index.PendingCount);
        Assert.Empty(rootStore.Written);

        // Past the interval: FlushIfDue flushes (no wall-clock sleep needed).
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(index.IsFlushDue());
        Ok(index.FlushIfDue());
        Assert.Equal(0, index.PendingCount);
        Assert.Single(rootStore.Written);
        AssertFound(index, 1, 1);
    }

    [Fact]
    public void TimeThreshold_FiresOnNextMutation_AfterIntervalElapses()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore,
            countThreshold: 1000, flushInterval: TimeSpan.FromSeconds(5), timeProvider: clock);

        Ok(index.Upsert(Key(1), Value(1)));
        clock.Advance(TimeSpan.FromSeconds(6));
        // The next mutation observes the elapsed interval and flushes the batch.
        Ok(index.Upsert(Key(2), Value(2)));
        Assert.Equal(0, index.PendingCount);
        Assert.Single(rootStore.Written);
        Assert.Equal(2, index.CommittedRoot!.EntryCount);
    }

    // --------------------------------------------------- Trigger: explicit

    [Fact]
    public void ExplicitFlush_CommitsBuffer_AndIsNoOpWhenEmpty()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        Ok(index.Upsert(Key(1), Value(1)));
        Ok(index.Upsert(Key(2), Value(2)));
        Assert.Null(index.CommittedRoot);

        Ok(index.Flush());
        Assert.Equal(0, index.PendingCount);
        Assert.Equal(2, index.CommittedRoot!.EntryCount);
        Assert.Single(rootStore.Written);

        // Flushing an empty buffer writes no new IndexRoot.
        Ok(index.Flush());
        Assert.Single(rootStore.Written);
    }

    // ------------------------------------------- Sort-by-key single COW pass

    [Fact]
    public void Flush_AppliesEntriesSortedByKey_InOneCowPass()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        // Buffer keys out of order; enough to force splits (height > 1).
        var keys = new[] { 40, 10, 30, 20, 50, 5, 25, 45, 15, 35 };
        foreach (var k in keys)
            Ok(index.Upsert(Key(k), Value(k)));
        Assert.Equal(keys.Length, index.PendingCount);

        Ok(index.Flush());

        // One flush => exactly one persisted IndexRoot (one COW pass, one root).
        Assert.Single(rootStore.Written);
        Assert.Equal(keys.Length, index.CommittedRoot!.EntryCount);
        Assert.True(index.CommittedRoot.Height >= 2, "Expected splits to grow height beyond 1.");

        // A range scan over the committed tree yields ascending key order — the
        // batch was applied in sorted order.
        var scan = _tree.Scan(index.CommittedRoot);
        var scanned = new List<int>();
        while (true)
        {
            var step = scan.MoveNext();
            Ok(step);
            if (!step.Value)
                break;
            scanned.Add(BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(28)));
        }
        var expected = keys.OrderBy(k => k).ToList();
        Assert.Equal(expected, scanned);
        foreach (var k in keys)
            AssertFound(index, k, k);
    }

    [Fact]
    public void Flush_AppliesBufferInAscendingKeyOrder_InOneCowPass()
    {
        // A wide leaf (no splits for this batch): the tree stays height 1 and a
        // single leaf block is appended per applied entry, so the store's append
        // log records the EXACT order entries were applied to the tree. This is
        // what makes the sort observable on the APPLY PATH -- a range scan of
        // the finished tree yields ascending order for ANY apply order, so it
        // cannot evidence that the flush itself applied entries sorted.
        var wideTree = new CowBTree(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 64, maxInternalKeys: 3);

        int fsyncCount = 0;
        var rootStore = NewRootStore();
        var index = new WalBufferedIndex(wideTree, rootStore,
            fsync: () => { fsyncCount++; return _manager.Flush(); },
            countThreshold: 1000);

        // Buffer keys in strictly DESCENDING order -- adversarial vs. the
        // required ascending apply order.
        var insertionOrder = new[] { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        foreach (var k in insertionOrder)
            Ok(index.Upsert(Key(k), Value(k)));
        // Nothing applied or committed yet: still one pending batch.
        Assert.Equal(insertionOrder.Length, index.PendingCount);
        Assert.Equal(0, fsyncCount);
        Assert.Empty(rootStore.Written);

        Ok(index.Flush());

        // ONE COW PASS: the whole batch commits as a SINGLE new root version --
        // exactly one fsync and exactly one persisted IndexRoot (one Sequence
        // bump), NOT one fsync/root per entry.
        Assert.Equal(1, fsyncCount);
        Assert.Single(rootStore.Written);
        Assert.Equal(IndexRoot.InitialSequence, index.Sequence);
        Assert.Equal(1, index.CommittedRoot!.Height); // wide leaf: no splits
        Assert.Equal(insertionOrder.Length, index.CommittedRoot.EntryCount);

        // SORTED APPLY ORDER: reconstruct the order entries were applied from the
        // store's leaf-block append log. Each successive appended leaf is the
        // running set after one more applied entry, so the first block a key
        // appears in is the step it was applied at -> first-appearance order
        // across the append log IS the apply order.
        var applyOrder = ReconstructApplyOrderFromLeafAppendLog();
        var ascending = insertionOrder.OrderBy(k => k).ToArray();
        Assert.Equal(ascending, applyOrder);
        // And decisively NOT the buffer's (descending) insertion order.
        Assert.NotEqual(insertionOrder, applyOrder);

        // End state is intact and readable.
        foreach (var k in insertionOrder)
            AssertFound(index, k, k);
    }

    /// <summary>
    /// Reads every BTreeLeaf block back in append (ascending-offset) order and
    /// returns the keys in first-appearance order — the order the flush applied
    /// buffered entries to the tree.
    /// </summary>
    private int[] ReconstructApplyOrderFromLeafAppendLog()
    {
        var scan = _manager.ScanForward();
        Ok(scan);
        var applyOrder = new List<int>();
        var seen = new HashSet<int>();
        foreach (var location in scan.Value.Blocks)
        {
            var read = _manager.ReadDecompressed(location.Offset);
            Ok(read);
            if (read.Value.Header.Type != BlockType.BTreeLeaf)
                continue;
            var leaf = EmailDB.Format.V3.BTreeNodeSerializer.DeserializeLeaf(read.Value.Payload);
            Ok(leaf);
            foreach (var entry in leaf.Value.Entries)
            {
                int key = BinaryPrimitives.ReadInt32BigEndian(entry.Key.AsSpan(28));
                if (seen.Add(key))
                    applyOrder.Add(key);
            }
        }
        return applyOrder.ToArray();
    }

    [Fact]
    public void Flush_LastWriteWinsPerKey_AndAppliesBufferedDeletes()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        // Seed a committed batch.
        for (int i = 1; i <= 5; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        Ok(index.Flush());
        Assert.Equal(5, index.CommittedRoot!.EntryCount);

        // Buffer: re-upsert 2 (new value), delete 4, re-add a fresh key 9.
        Ok(index.Upsert(Key(2), Value(222)));
        Ok(index.Delete(Key(4)));
        Ok(index.Upsert(Key(9), Value(9)));
        // Read-your-writes before the flush.
        AssertFound(index, 2, 222);
        AssertAbsent(index, 4);
        AssertFound(index, 9, 9);

        Ok(index.Flush());
        Assert.Equal(5, index.CommittedRoot!.EntryCount); // +1 (key 9) -1 (key 4)
        AssertFound(index, 2, 222);
        AssertAbsent(index, 4);
        AssertFound(index, 9, 9);
        AssertFound(index, 1, 1);
    }

    // ----------------------------------------------- Sequence monotonicity

    [Fact]
    public void IndexRootSequence_IncrementsMonotonically_PerFlush()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        Assert.Null(index.Sequence);

        Ok(index.Upsert(Key(1), Value(1)));
        Ok(index.Flush());
        Assert.Equal(IndexRoot.InitialSequence, index.Sequence);
        Assert.Equal(BTreeIndexKind.PrimaryEmail, index.CommittedIndexRoot!.IndexKind);

        for (ulong expected = 1; expected <= 4; expected++)
        {
            Ok(index.Upsert(Key((int)expected + 1), Value((int)expected + 1)));
            Ok(index.Flush());
            Assert.Equal(expected, index.Sequence);
        }

        // Persisted IndexRoots carry strictly increasing sequences, all same kind.
        var sequences = rootStore.Written.Select(r => r.Sequence).ToList();
        Assert.Equal(new ulong[] { 0, 1, 2, 3, 4 }, sequences);
        Assert.All(rootStore.Written, r => Assert.Equal(BTreeIndexKind.PrimaryEmail, r.IndexKind));
        // The committed IndexRoot matches the committed tree root.
        Assert.Equal(index.CommittedRoot!.RootRef.Reference, index.CommittedIndexRoot!.RootBlockId);
        Assert.Equal(index.CommittedRoot.EntryCount, index.CommittedIndexRoot.EntryCount);
    }

    [Fact]
    public void TwoIndexes_FlushingInterleaved_KeepIndependentMonotonicSequences()
    {
        // Two BlockId-addressed indexes of different kinds share the same block
        // stream (one BlockManager/store). Each WalBufferedIndex tracks its own
        // committed IndexRoot, so its Sequence is derived from ITS OWN previous
        // version -- one index's flushes must never advance the other's chain.
        var primaryTree = new CowBTree(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 4, maxInternalKeys: 3);
        var dateTree = new CowBTree(_store, BTreeIndexKind.Date, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 4, maxInternalKeys: 3);

        var primaryStore = NewRootStore();
        var dateStore = NewRootStore();
        var primary = new WalBufferedIndex(primaryTree, primaryStore, _manager.Flush, countThreshold: 1000);
        var date = new WalBufferedIndex(dateTree, dateStore, _manager.Flush, countThreshold: 1000);

        // Interleave flushes: A, B, A, A, B. A flushes three times, B twice.
        int keySeed = 1;
        void FlushPrimary() { Ok(primary.Upsert(Key(keySeed), Value(keySeed))); keySeed++; Ok(primary.Flush()); }
        void FlushDate() { Ok(date.Upsert(Key(keySeed), Value(keySeed))); keySeed++; Ok(date.Flush()); }

        FlushPrimary();                       // A -> Seq 0
        Assert.Equal(IndexRoot.InitialSequence, primary.Sequence);
        Assert.Null(date.Sequence);           // B untouched by A's flush

        FlushDate();                          // B -> Seq 0
        Assert.Equal(IndexRoot.InitialSequence, date.Sequence);
        Assert.Equal(IndexRoot.InitialSequence, primary.Sequence); // A not bumped by B

        FlushPrimary();                       // A -> Seq 1
        Assert.Equal((ulong)1, primary.Sequence);
        Assert.Equal((ulong)0, date.Sequence);

        FlushPrimary();                       // A -> Seq 2
        Assert.Equal((ulong)2, primary.Sequence);
        Assert.Equal((ulong)0, date.Sequence); // B's chain still at its own last value

        FlushDate();                          // B -> Seq 1
        Assert.Equal((ulong)1, date.Sequence);
        Assert.Equal((ulong)2, primary.Sequence);

        // Each index's persisted IndexRoots form its own strictly increasing
        // chain, carrying only its own IndexKind.
        Assert.Equal(new ulong[] { 0, 1, 2 }, primaryStore.Written.Select(r => r.Sequence).ToArray());
        Assert.Equal(new ulong[] { 0, 1 }, dateStore.Written.Select(r => r.Sequence).ToArray());
        Assert.All(primaryStore.Written, r => Assert.Equal(BTreeIndexKind.PrimaryEmail, r.IndexKind));
        Assert.All(dateStore.Written, r => Assert.Equal(BTreeIndexKind.Date, r.IndexKind));
    }

    // -------------------------------------- Failure contract: IndexRoot write

    [Fact]
    public void FailedIndexRootWrite_LeavesPreviousRootAuthoritative_BufferRetained()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        // Commit a first batch successfully.
        for (int i = 1; i <= 3; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        Ok(index.Flush());
        var authoritativeRoot = index.CommittedRoot;
        var authoritativeIndexRoot = index.CommittedIndexRoot;
        long fileLengthBefore = new FileInfo(_path).Length;

        // Buffer a second batch and force the IndexRoot write to fail.
        for (int i = 4; i <= 8; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        rootStore.FailNext = true;

        var flush = index.Flush();
        Assert.True(flush.IsFailure);
        Assert.Contains("authoritative", flush.Error);

        // Previous root/IndexRoot unchanged and still fully readable.
        Assert.Same(authoritativeRoot, index.CommittedRoot);
        Assert.Same(authoritativeIndexRoot, index.CommittedIndexRoot);
        Assert.Equal(IndexRoot.InitialSequence, index.Sequence);
        Assert.Single(rootStore.Written);
        for (int i = 1; i <= 3; i++)
            AssertFound(index, i, i);

        // Buffer retained: the new keys are still pending (read-your-writes).
        Assert.Equal(5, index.PendingCount);
        AssertFound(index, 8, 8);

        // Orphan nodes were written before the failure (file grew) but nothing
        // references them; the committed tree ignores them.
        Assert.True(new FileInfo(_path).Length > fileLengthBefore,
            "Failed flush should have appended orphan node blocks.");

        // Retry succeeds: the retained batch commits and Sequence advances once.
        rootStore.FailNext = false;
        Ok(index.Flush());
        Assert.Equal(0, index.PendingCount);
        Assert.Equal((ulong)1, index.Sequence);
        for (int i = 1; i <= 8; i++)
            AssertFound(index, i, i);
    }

    // ------------------------------------------ Failure contract: node write

    [Fact]
    public void FailedNodeWrite_LeavesPreviousRootAuthoritative_BufferRetained()
    {
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        for (int i = 1; i <= 3; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        Ok(index.Flush());
        var authoritativeRoot = index.CommittedRoot;

        // Buffer a batch, then poison the block writes so applying the batch
        // fails on the very first node append.
        for (int i = 4; i <= 8; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        _stream.FailWrites = true;

        var flush = index.Flush();
        Assert.True(flush.IsFailure);

        // The block stream is now poisoned; the committed root stays the
        // pre-flush snapshot and the buffer is retained.
        Assert.Same(authoritativeRoot, index.CommittedRoot);
        Assert.Equal(IndexRoot.InitialSequence, index.Sequence);
        Assert.Single(rootStore.Written);
        Assert.Equal(5, index.PendingCount);

        // The previously committed keys remain readable from the authoritative
        // root (reads never poison), and the buffered-but-unflushed keys still
        // read from the retained buffer (read-your-writes survives the failure).
        for (int i = 1; i <= 3; i++)
            AssertFound(index, i, i);
        AssertFound(index, 8, 8);

        // "Reclaimed later" boundary: a failed NODE write (or fsync) FATALLY
        // poisons the block stream by design (spec Section 10.3 — a torn append
        // is never buried), so an in-process retry is refused; the batch is
        // re-appliable only after close + crash recovery. The store-level
        // IndexRoot-write failure (FailedIndexRootWrite_...) is the non-poisoning
        // path that demonstrates a successful in-process retry. Compaction that
        // physically reclaims the orphans is not yet implemented; the tested
        // contract here is that the orphans are inert — see
        // FailedIndexRootWrite_OrphanNodesAreInert_....
    }

    // -------------------------------------- Failure contract: orphan inertness

    [Fact]
    public void FailedIndexRootWrite_OrphanNodesAreInert_AuthoritativeTreeScansExactlyCommitted()
    {
        // A failed flush leaves fully written node blocks on disk that no
        // authoritative root references ("orphans reclaimed later"). Compaction
        // that physically reclaims them is not yet implemented, so the testable
        // contract is that they are INERT: the authoritative root full-tree
        // verifies without ever visiting them, and a scan of it returns EXACTLY
        // the committed entries — the orphans are safely identifiable/ignorable.
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        // Commit a small batch: with maxLeafEntries=4 the authoritative tree is a
        // single leaf (one node), so any orphan blocks are clearly extra.
        for (int i = 1; i <= 3; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        Ok(index.Flush());
        var authoritativeRoot = index.CommittedRoot!;
        long fileLengthBeforeFailedFlush = new FileInfo(_path).Length;

        // Buffer 5 more keys (forces splits -> multiple new nodes), let every
        // node write and the fsync succeed, then fail only the IndexRoot write.
        for (int i = 4; i <= 8; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        rootStore.FailNext = true;
        Assert.True(index.Flush().IsFailure);

        // The whole working tree (root + leaves for keys 1..8) was appended and
        // fsynced before the IndexRoot write failed: those blocks are now orphans.
        Assert.True(new FileInfo(_path).Length > fileLengthBeforeFailedFlush,
            "Failed flush should have appended orphan node blocks.");

        // Authoritative root is unchanged and fully intact: verification walks
        // ONLY its nodes (the single committed leaf) and never touches an orphan.
        Assert.Same(authoritativeRoot, index.CommittedRoot);
        var verification = _tree.VerifyFullTree(authoritativeRoot);
        Assert.True(verification.IsIntact, verification.Error);
        Assert.Equal(1, authoritativeRoot.Height);       // still the single-leaf tree
        Assert.Equal(1, verification.NodesVerified);      // exactly one node visited

        // A scan of the authoritative tree yields EXACTLY the committed entries;
        // the orphaned nodes carrying keys 4..8 are unreferenced and never seen.
        Assert.Equal(new[] { 1, 2, 3 }, ScanKeys(authoritativeRoot).ToArray());

        // And the orphans stay inert across a successful retry: the retained
        // batch re-applies onto the authoritative root and commits cleanly.
        rootStore.FailNext = false;
        Ok(index.Flush());
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ScanKeys(index.CommittedRoot).ToArray());
        Assert.True(_tree.VerifyFullTree(index.CommittedRoot).IsIntact);
    }

    // ------------------------------------------------ Failure contract: fsync

    [Fact]
    public void FailedFsync_LeavesPreviousRootAuthoritative_BufferRetained()
    {
        // The spec write order is nodes -> fsync -> IndexRoot; a failure at the
        // fsync step must be as harmless as a node or IndexRoot failure. fsync
        // failure is FATAL (spec Section 10.3) so there is no in-process retry,
        // but the previous root stays authoritative, the buffer is retained, and
        // the freshly written nodes are inert orphans.
        var rootStore = NewRootStore();
        var index = CreateIndex(rootStore, countThreshold: 1000);

        for (int i = 1; i <= 3; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        Ok(index.Flush());
        var authoritativeRoot = index.CommittedRoot;
        var authoritativeIndexRoot = index.CommittedIndexRoot;
        long fileLengthBefore = new FileInfo(_path).Length;

        // Buffer a second batch; fail the fsync that follows the node writes.
        for (int i = 4; i <= 8; i++)
            Ok(index.Upsert(Key(i), Value(i)));
        _stream.FailNextFsyncs = 1;

        var flush = index.Flush();
        Assert.True(flush.IsFailure);
        Assert.Contains("fsync", flush.Error);
        Assert.Contains("authoritative", flush.Error);

        // No IndexRoot was written (the failure preceded it); previous root and
        // IndexRoot remain authoritative and readable, and the buffer is retained.
        Assert.Same(authoritativeRoot, index.CommittedRoot);
        Assert.Same(authoritativeIndexRoot, index.CommittedIndexRoot);
        Assert.Equal(IndexRoot.InitialSequence, index.Sequence);
        Assert.Single(rootStore.Written);
        Assert.Equal(5, index.PendingCount);
        for (int i = 1; i <= 3; i++)
            AssertFound(index, i, i);
        AssertFound(index, 8, 8); // buffered read-your-writes survives the failure

        // Nodes written before the fsync are orphans on disk, and the
        // authoritative tree ignores them (scans exactly the committed entries).
        Assert.True(new FileInfo(_path).Length > fileLengthBefore,
            "Node blocks written before the failed fsync should be on disk as orphans.");
        Assert.Equal(new[] { 1, 2, 3 }, ScanKeys(authoritativeRoot).ToArray());
        Assert.True(_tree.VerifyFullTree(authoritativeRoot).IsIntact);
    }

    // ---------------------------------------------------- Argument validation

    [Fact]
    public void Upsert_WrongWidths_Throw_AndOffsetIndexRejected()
    {
        var index = CreateIndex(NewRootStore(), countThreshold: 1000);
        Assert.Throws<ArgumentException>(() => index.Upsert(new byte[5], Value(1)));
        Assert.Throws<ArgumentException>(() => index.Upsert(Key(1), new byte[3]));
        Assert.Throws<ArgumentException>(() => index.Delete(new byte[5]));

        // BlockLocation is offset-addressed: not supported by this flush layer.
        var locationTree = new CowBTree(_store, BTreeIndexKind.BlockLocation, keySize: 16, leafValueSize: 16,
            maxLeafEntries: 4, maxInternalKeys: 3);
        Assert.Throws<ArgumentException>(() =>
            new WalBufferedIndex(locationTree, NewRootStore(), _manager.Flush));
    }
}
