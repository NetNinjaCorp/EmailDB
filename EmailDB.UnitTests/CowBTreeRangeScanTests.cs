using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 copy-on-write B+-tree range scan (US-EMDB-68-8,
/// docs/BTree_Index.md Sections 1, 5, 7): sorted [start, end) iteration and
/// full-tree iteration across leaf boundaries via PARENT BACKTRACKING — the
/// format has no sibling pointers, so leaf-to-leaf advancement climbs the
/// parent stack to the nearest ancestor with an unvisited child and descends
/// to its leftmost leaf. Every traversed node is Merkle-verified; old roots
/// scan as consistent snapshots after further mutations.
/// </summary>
public class CowBTreeRangeScanTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-scan-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeRangeScanTests()
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
    }

    /// <summary>
    /// PrimaryEmail-shaped tree (32-byte keys, 16-byte values) with tiny
    /// capacities so modest key counts produce many leaves and height >= 3 —
    /// scans must cross plenty of leaf and subtree boundaries.
    /// </summary>
    private CowBTree CreateTree(int maxLeafEntries = 2, int maxInternalKeys = 2) =>
        new(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

    /// <summary>Fixed-width key whose lexicographic order equals numeric order of <paramref name="i"/>.</summary>
    private static byte[] Key(int width, int i)
    {
        var key = new byte[width];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(width - 4), i);
        return key;
    }

    private static byte[] Key32(int i) => Key(32, i);

    private static byte[] Value16(int i)
    {
        var value = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(12), i);
        return value;
    }

    private static BTreeRoot InsertOk(CowBTree tree, BTreeRoot? root, byte[] key, byte[] value)
    {
        var result = tree.Insert(root, key, value);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        return result.Value;
    }

    private static BTreeRoot InsertRange(CowBTree tree, BTreeRoot? root, IEnumerable<int> keys)
    {
        foreach (var i in keys)
            root = InsertOk(tree, root, Key32(i), Value16(i));
        return root!;
    }

    private static BTreeRoot? DeleteOk(CowBTree tree, BTreeRoot root, int i)
    {
        var result = tree.Delete(root, Key32(i));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.True(result.Value.Removed, $"Key {i} should have been present to delete.");
        return result.Value.Root;
    }

    /// <summary>
    /// Drains a scan to completion, decoding each 32-byte key back to its int
    /// seed and asserting each value matches its key's seed (every test
    /// inserts Value16(i) for Key32(i) unless it says otherwise).
    /// </summary>
    private static List<int> DrainKeys(CowBTree.RangeScan scan, bool checkValues = true)
    {
        var keys = new List<int>();
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            Assert.Equal(32, scan.Current.Key.Length);
            int seed = BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(28));
            if (checkValues)
                Assert.Equal(Value16(seed), scan.Current.Value);
            keys.Add(seed);
        }
        return keys;
    }

    // ---- Sorted iteration across leaf boundaries (parent backtracking) ----

    [Fact]
    public void FullScan_RandomInsertOrder_ReturnsEveryKeySortedAcrossManyLeaves()
    {
        var tree = CreateTree();
        var seeds = Enumerable.Range(1, 200).ToArray();
        new Random(42).Shuffle(seeds);
        var root = InsertRange(tree, null, seeds);

        // maxLeafEntries = 2 forces ~100 leaves; the unbounded scan must cross
        // every leaf boundary by backtracking (no sibling pointers exist).
        Assert.True(root.Height >= 3, $"Expected height >= 3, got {root.Height}.");

        var keys = DrainKeys(tree.Scan(root));

        Assert.Equal(Enumerable.Range(1, 200), keys);
    }

    [Fact]
    public void BoundedScan_CrossesMultipleLeafAndSubtreeBoundaries_SortedWithinRange()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 100));
        Assert.True(root.Height >= 3, $"Expected height >= 3, got {root.Height}.");

        // [17, 63) spans ~23 two-entry leaves and multiple internal subtrees:
        // start mid-leaf, end mid-leaf.
        var keys = DrainKeys(tree.Scan(root, Key32(17), Key32(63)));

        Assert.Equal(Enumerable.Range(17, 46), keys); // 17..62
    }

    [Fact]
    public void BoundedScan_StartIsInclusive_EndIsExclusive()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 30));

        var keys = DrainKeys(tree.Scan(root, Key32(10), Key32(20)));

        Assert.Equal(Enumerable.Range(10, 10), keys); // 10..19: 10 in, 20 out
    }

    [Fact]
    public void BoundedScan_AbsentBoundKeys_SnapToKeysInsideRange()
    {
        var tree = CreateTree();
        // Even keys only: 2, 4, ..., 60.
        var root = InsertRange(tree, null, Enumerable.Range(1, 30).Select(i => i * 2));

        // Odd bounds exist nowhere in the tree: [15, 45) must yield 16..44 even.
        var keys = DrainKeys(tree.Scan(root, Key32(15), Key32(45)));

        Assert.Equal(Enumerable.Range(8, 15).Select(i => i * 2), keys); // 16..44
    }

    [Fact]
    public void Scan_SingleLeafTree_Works()
    {
        var tree = CreateTree(maxLeafEntries: 8, maxInternalKeys: 3);
        var root = InsertRange(tree, null, [3, 1, 2]);
        Assert.Equal(1, root.Height);

        Assert.Equal([1, 2, 3], DrainKeys(tree.Scan(root)));
        Assert.Equal([2], DrainKeys(tree.Scan(root, Key32(2), Key32(3))));
    }

    [Fact]
    public void BoundedScan_EveryAdjacentPair_CrossesItsBoundary_IncludingMultiLevelBacktracks()
    {
        // Deterministic multi-level tree: sequential inserts, 2-entry nodes.
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 100));
        Assert.True(root.Height >= 3, $"Expected height >= 3, got {root.Height}.");

        // For every adjacent key pair, a [k, k+2) scan seeks to k and must then
        // advance across WHATEVER boundary separates k from k+1: none (same
        // leaf), a 1-level backtrack (sibling leaf under one parent), or a
        // multi-level backtrack that pops >= 2 exhausted frames when k is the
        // last key of an entire subtree — including the root's own subtree
        // boundaries in a height >= 3 tree. Sweeping all pairs guarantees every
        // boundary in the tree, of every climb depth, is crossed as the scan's
        // very next step and yields the correct successor.
        for (int k = 1; k < 100; k++)
        {
            var keys = DrainKeys(tree.Scan(root, Key32(k), Key32(k + 2)));
            Assert.Equal([k, k + 1], keys);
        }
    }

    // ---- Scans after deletes have restructured the tree ----

    [Fact]
    public void Scan_AfterDeletesRestructureTree_StrictlyAscendingAcrossEveryBoundary()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 120));
        int originalHeight = root.Height;
        Assert.True(originalHeight >= 3, $"Expected height >= 3, got {originalHeight}.");

        // Restructure heavily: chop off the whole right flank (91..120), then
        // every third survivor — merges, borrows, and root-collapse pressure.
        BTreeRoot? current = root;
        for (int i = 91; i <= 120; i++)
            current = DeleteOk(tree, current!, i);
        for (int i = 3; i <= 90; i += 3)
            current = DeleteOk(tree, current!, i);

        Assert.NotNull(current);
        Assert.True(current!.Height >= 2, "Restructured tree should still span multiple leaves.");
        Assert.True(current.Height <= originalHeight, "Height must not grow from deletes.");

        // Full scan: raw keys strictly ascending byte-wise across EVERY
        // consecutive pair — i.e. across every leaf boundary the backtracking
        // crosses in the restructured tree — and exactly the survivors.
        var scan = tree.Scan(current);
        byte[]? previousKey = null;
        var keys = new List<int>();
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            if (previousKey is not null)
                Assert.True(previousKey.AsSpan().SequenceCompareTo(scan.Current.Key) < 0,
                    "Scan keys must be strictly ascending across every leaf boundary.");
            previousKey = scan.Current.Key;
            keys.Add(BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(28)));
        }
        var expected = Enumerable.Range(1, 90).Where(i => i % 3 != 0).ToList();
        Assert.Equal(expected, keys);

        // Bounded scan on the restructured tree still honors [start, end).
        var bounded = DrainKeys(tree.Scan(current, Key32(20), Key32(50)));
        Assert.Equal(expected.Where(i => i is >= 20 and < 50), bounded);
    }

    // ---- Unbounded and half-bounded scans ----

    [Fact]
    public void Scan_StartOnly_RunsThroughLargestKey()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 50));

        var keys = DrainKeys(tree.Scan(root, startInclusive: Key32(43)));

        Assert.Equal(Enumerable.Range(43, 8), keys); // 43..50
    }

    [Fact]
    public void Scan_EndOnly_StartsAtSmallestKey()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 50));

        var keys = DrainKeys(tree.Scan(root, endExclusive: Key32(8)));

        Assert.Equal(Enumerable.Range(1, 7), keys); // 1..7
    }

    // ---- Empty results ----

    [Fact]
    public void Scan_EmptyTree_NullRoot_YieldsNothing()
    {
        var tree = CreateTree();

        Assert.Empty(DrainKeys(tree.Scan(null)));
        Assert.Empty(DrainKeys(tree.Scan(null, Key32(1), Key32(100))));
    }

    [Fact]
    public void Scan_StartEqualsEnd_YieldsNothing()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 30));

        Assert.Empty(DrainKeys(tree.Scan(root, Key32(10), Key32(10))));
    }

    [Fact]
    public void Scan_InvertedRange_YieldsNothing()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 30));

        Assert.Empty(DrainKeys(tree.Scan(root, Key32(20), Key32(10))));
    }

    [Fact]
    public void Scan_RangeEntirelyOutsideKeys_YieldsNothing()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(10, 20)); // 10..29

        Assert.Empty(DrainKeys(tree.Scan(root, Key32(1), Key32(10))));   // below all
        Assert.Empty(DrainKeys(tree.Scan(root, Key32(30), Key32(99))));  // above all
    }

    [Fact]
    public void MoveNext_AfterExhaustion_KeepsReturningFalse()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));

        var scan = tree.Scan(root);
        Assert.Equal(5, DrainKeys(scan).Count);

        var again = scan.MoveNext();
        Assert.True(again.IsSuccess, again.IsFailure ? again.Error : null);
        Assert.False(again.Value);
    }

    // ---- Old roots remain scannable snapshots ----

    [Fact]
    public void OldRoot_ScansIdentically_AfterFurtherInsertsUpdatesAndDeletes()
    {
        var tree = CreateTree();

        var rootA = InsertRange(tree, null, Enumerable.Range(1, 40));

        // Mutate heavily on top of rootA: more inserts (root splits), a value
        // update, and deletes that trigger merges and root collapse pressure.
        var rootB = InsertRange(tree, rootA, Enumerable.Range(41, 40));
        rootB = InsertOk(tree, rootB, Key32(5), Value16(555));
        for (int i = 1; i <= 80; i += 2)
            rootB = DeleteOk(tree, rootB!, i)!;

        // rootA: still exactly 1..40 in order, original value of key 5.
        var scanA = tree.Scan(rootA);
        var keysA = new List<int>();
        while (true)
        {
            var step = scanA.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            int seed = BinaryPrimitives.ReadInt32BigEndian(scanA.Current.Key.AsSpan(28));
            Assert.Equal(Value16(seed), scanA.Current.Value); // pre-update values
            keysA.Add(seed);
        }
        Assert.Equal(Enumerable.Range(1, 40), keysA);

        // rootB: only the surviving even keys 2..80, sorted.
        var keysB = DrainKeys(tree.Scan(rootB), checkValues: false);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => i * 2), keysB);

        // Bounded scan on the old root also honors [start, end).
        Assert.Equal(Enumerable.Range(12, 9), DrainKeys(tree.Scan(rootA, Key32(12), Key32(21))));
    }

    /// <summary>
    /// The COW acceptance criterion for US-EMDB-68 in one test: a chain of N
    /// snapshots, each captured after a generation of MIXED mutations
    /// (inserts, value updates, and rebalancing deletes), and each verified in
    /// FULL only after all N generations completed — every live key via
    /// Merkle-verified TryGet with its generation-correct value, a full range
    /// scan returning exactly the snapshot's sorted key set, and snapshot
    /// isolation: every key that was deleted by, or inserted after, that
    /// generation must be invisible to it.
    /// </summary>
    [Fact]
    public void SnapshotChain_EveryGeneration_FullyReadableAndIsolated_AfterAllMixedMutations()
    {
        var tree = CreateTree();
        var random = new Random(20260704);

        const int generations = 8;
        const int insertsPerGen = 15;
        var model = new Dictionary<int, int>(); // key seed -> current value seed
        var snapshots = new List<(BTreeRoot Root, Dictionary<int, int> Model)>();

        BTreeRoot? root = null;
        int nextKey = 1;
        for (int gen = 1; gen <= generations; gen++)
        {
            // Inserts: brand-new keys (forces splits and root growth).
            for (int n = 0; n < insertsPerGen; n++)
            {
                int key = nextKey++;
                root = InsertOk(tree, root, Key32(key), Value16(key));
                model[key] = key;
            }

            var live = model.Keys.ToArray();

            // Updates: existing keys get generation-tagged values (COW upsert).
            for (int n = 0; n < 5; n++)
            {
                int key = live[random.Next(live.Length)];
                int value = 10_000 * gen + key;
                root = InsertOk(tree, root!, Key32(key), Value16(value));
                model[key] = value;
            }

            // Deletes: distinct existing keys (forces borrows/merges/collapses).
            foreach (int key in live.OrderBy(_ => random.Next()).Take(5))
            {
                root = DeleteOk(tree, root!, key);
                model.Remove(key);
            }

            snapshots.Add((root!, new Dictionary<int, int>(model)));
        }

        // Tiny fan-out plus ~100 live keys guarantees deep trees, so the
        // mutations above rewrote paths at every level many times over.
        Assert.True(root!.Height >= 3, $"Expected height >= 3, got {root.Height}.");

        // Only now — after ALL generations mutated on top of every snapshot —
        // verify each generation's root in full.
        for (int gen = 0; gen < generations; gen++)
        {
            var (snapRoot, snapModel) = snapshots[gen];
            Assert.Equal(snapModel.Count, snapRoot.EntryCount);

            // Full scan: exactly this snapshot's keys, sorted, with the values
            // as of this generation (later updates must be invisible).
            var scan = tree.Scan(snapRoot);
            var scanned = new List<int>();
            while (true)
            {
                var step = scan.MoveNext();
                Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
                if (!step.Value)
                    break;
                int key = BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(28));
                Assert.True(snapModel.ContainsKey(key),
                    $"Gen {gen + 1} snapshot scanned key {key} it should not contain.");
                Assert.Equal(Value16(snapModel[key]), scan.Current.Value);
                scanned.Add(key);
            }
            Assert.Equal(snapModel.Keys.OrderBy(k => k), scanned);

            // Every live key individually via Merkle-verified point lookup.
            foreach (var (key, value) in snapModel)
            {
                var found = tree.TryGet(snapRoot, Key32(key));
                Assert.True(found.IsSuccess, found.IsFailure ? found.Error : null);
                Assert.True(found.Value.Found, $"Gen {gen + 1} snapshot lost key {key}.");
                Assert.Equal(Value16(value), found.Value.Value);
            }

            // Isolation: every key ever minted that is NOT in this snapshot —
            // deleted by this generation or inserted by a later one — must be
            // absent from it.
            for (int key = 1; key < nextKey; key++)
            {
                if (snapModel.ContainsKey(key))
                    continue;
                var absent = tree.TryGet(snapRoot, Key32(key));
                Assert.True(absent.IsSuccess, absent.IsFailure ? absent.Error : null);
                Assert.False(absent.Value.Found,
                    $"Gen {gen + 1} snapshot must not see key {key} (deleted earlier or inserted later).");
            }
        }
    }

    // ---- Other index layouts ----

    [Fact]
    public void Scan_DateKind_KeyOnlyEntries_SortedWithEmptyValues()
    {
        // Date index (spec Section 6.1): 24-byte composite key, empty value.
        var tree = new CowBTree(_store, BTreeIndexKind.Date, keySize: 24, leafValueSize: 0,
            maxLeafEntries: 2, maxInternalKeys: 2);

        BTreeRoot? root = null;
        var seeds = Enumerable.Range(1, 40).ToArray();
        new Random(7).Shuffle(seeds);
        foreach (var i in seeds)
            root = InsertOk(tree, root, Key(24, i), []);

        var scan = tree.Scan(root, Key(24, 10), Key(24, 30));
        var keys = new List<int>();
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            Assert.Empty(scan.Current.Value);
            keys.Add(BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(20)));
        }
        Assert.Equal(Enumerable.Range(10, 20), keys); // 10..29
    }

    // ---- Merkle verification and argument validation ----

    [Fact]
    public void Scan_TamperedRootHash_FailsMerkleVerification()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 20));

        var tamperedHash = (byte[])root.RootRef.NodeHash.Clone();
        tamperedHash[0] ^= 0xFF;
        var tamperedRoot = new BTreeRoot
        {
            RootRef = new BTreeNodeRef
            {
                Addressing = root.RootRef.Addressing,
                Reference = root.RootRef.Reference,
                NodeHash = tamperedHash,
            },
            Height = root.Height,
            EntryCount = root.EntryCount,
        };

        var scan = tree.Scan(tamperedRoot);
        var step = scan.MoveNext();
        Assert.True(step.IsFailure);
        Assert.Contains("Merkle", step.Error);

        // A failed scan is dead: further calls report exhaustion, not entries.
        var after = scan.MoveNext();
        Assert.True(after.IsSuccess);
        Assert.False(after.Value);
    }

    [Fact]
    public void Scan_WrongBoundWidth_Throws()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));

        Assert.Throws<ArgumentException>(() => tree.Scan(root, startInclusive: new byte[5]));
        Assert.Throws<ArgumentException>(() => tree.Scan(root, endExclusive: new byte[31]));
    }
}
