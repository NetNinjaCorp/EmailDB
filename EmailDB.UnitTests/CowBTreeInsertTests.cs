using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 copy-on-write B+-tree insert path (US-EMDB-68-6,
/// docs/BTree_Index.md Sections 1, 4; EmailDB_FileFormat_Spec.md Section 6):
/// root-to-leaf COW path rewrite as new blocks, leaf/internal splits with key
/// promotion, root splits growing height by exactly 1, structural sharing of
/// unchanged subtrees, old roots remaining fully readable snapshots, and
/// Merkle path verification of every traversed node.
/// </summary>
public class CowBTreeInsertTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeInsertTests()
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
    /// PrimaryEmail-shaped tree (32-byte keys, 16-byte values, BlockId-addressed
    /// children) with tiny capacities so a handful of inserts forces splits.
    /// </summary>
    private CowBTree CreateTree(int maxLeafEntries = 4, int maxInternalKeys = 3) =>
        new(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

    /// <summary>Fixed-width key whose lexicographic order equals numeric order of <paramref name="i"/>.</summary>
    private static byte[] Key(int width, int i)
    {
        var key = new byte[width];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(width - 4), i);
        return key;
    }

    private static byte[] Value(int width, int i)
    {
        var value = new byte[width];
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(width - 4), i);
        return value;
    }

    private static byte[] Key32(int i) => Key(32, i);
    private static byte[] Value16(int i) => Value(16, i);

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

    private static void AssertFound(CowBTree tree, BTreeRoot root, int i, int? expectedValueSeed = null)
    {
        var result = tree.TryGet(root, Key32(i));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.True(result.Value.Found, $"Key {i} should be present.");
        Assert.Equal(Value16(expectedValueSeed ?? i), result.Value.Value);
    }

    private static void AssertNotFound(CowBTree tree, BTreeRoot root, int i)
    {
        var result = tree.TryGet(root, Key32(i));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.False(result.Value.Found, $"Key {i} should be absent.");
    }

    /// <summary>Reads and deserializes an internal root node for structural assertions.</summary>
    private BTreeInternalNode ReadInternalRoot(BTreeRoot root)
    {
        var payload = _store.ReadVerified(root.RootRef, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        return node.Value;
    }

    // ---- Empty tree and no-split inserts ----

    [Fact]
    public void Insert_IntoEmptyTree_CreatesSingleLeafRoot()
    {
        var tree = CreateTree();

        var root = InsertOk(tree, null, Key32(1), Value16(1));

        Assert.Equal(1, root.Height);
        Assert.Equal(1, root.EntryCount);
        AssertFound(tree, root, 1);
        AssertNotFound(tree, root, 2);
    }

    [Fact]
    public void Insert_UpToLeafCapacity_KeepsHeightOne()
    {
        var tree = CreateTree(maxLeafEntries: 4);

        var root = InsertRange(tree, null, Enumerable.Range(1, 4));

        Assert.Equal(1, root.Height);
        Assert.Equal(4, root.EntryCount);
        for (int i = 1; i <= 4; i++)
            AssertFound(tree, root, i);
    }

    // ---- Leaf split and root split ----

    [Fact]
    public void Insert_LeafOverflow_SplitsAndGrowsHeightToTwo()
    {
        var tree = CreateTree(maxLeafEntries: 4);

        var root = InsertRange(tree, null, Enumerable.Range(1, 5));

        Assert.Equal(2, root.Height);
        Assert.Equal(5, root.EntryCount);
        for (int i = 1; i <= 5; i++)
            AssertFound(tree, root, i);

        // The new root is an internal node with exactly one promoted separator
        // key and two children (root split, BTree_Index.md Section 4).
        var rootNode = ReadInternalRoot(root);
        Assert.Single(rootNode.Keys);
        Assert.Equal(2, rootNode.Children.Count);
    }

    [Fact]
    public void Insert_SplitsPropagateUpward_HeightGrowsByExactlyOnePerRootSplit()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);

        BTreeRoot? root = null;
        int previousHeight = 0;
        for (int i = 1; i <= 60; i++)
        {
            root = InsertOk(tree, root, Key32(i), Value16(i));
            Assert.True(root.Height >= previousHeight, "Height must never shrink on insert.");
            Assert.True(root.Height - previousHeight <= 1,
                $"Height jumped from {previousHeight} to {root.Height} on a single insert.");
            previousHeight = root.Height;
        }

        // Tiny fan-out guarantees multi-level split propagation.
        Assert.True(root!.Height >= 3, $"Expected height >= 3 after 60 inserts at fan-out 3, got {root.Height}.");
        Assert.Equal(60, root.EntryCount);
        for (int i = 1; i <= 60; i++)
            AssertFound(tree, root, i);
    }

    [Fact]
    public void Insert_RandomOrder_AllKeysRetrievable()
    {
        var tree = CreateTree();
        var keys = Enumerable.Range(1, 200).ToArray();
        var random = new Random(42);
        random.Shuffle(keys);

        var root = InsertRange(tree, null, keys);

        Assert.Equal(200, root.EntryCount);
        Assert.True(root.Height >= 3);
        for (int i = 1; i <= 200; i++)
            AssertFound(tree, root, i);
        AssertNotFound(tree, root, 0);
        AssertNotFound(tree, root, 201);
        AssertNotFound(tree, root, 5000);
    }

    /// <summary>Reads and deserializes an internal (non-root) child node from its raw child record.</summary>
    private BTreeInternalNode ReadInternalChild(CowBTree tree, byte[] childRecord)
    {
        var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, childRecord);
        Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
        var payload = _store.ReadVerified(childRef.Value, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        return node.Value;
    }

    [Fact]
    public void Insert_AtExactCapacityBoundaries_HeightGrowsOneToTwoToThree()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);

        // Fill the root leaf exactly to capacity: height stays 1.
        var root = InsertRange(tree, null, Enumerable.Range(1, 2));
        Assert.Equal(1, root.Height);

        // One more insert overflows the leaf: root split, height 1 -> 2. The
        // promoted separator is a copy of the right half's first key (2).
        root = InsertOk(tree, root, Key32(3), Value16(3));
        Assert.Equal(2, root.Height);
        var rootNode = ReadInternalRoot(root);
        Assert.Single(rootNode.Keys);
        Assert.Equal(Key32(2), rootNode.Keys[0]);

        // Insert 4 splits another leaf but the root absorbs the separator:
        // exactly at maxInternalKeys, no root split, height stays 2.
        root = InsertOk(tree, root, Key32(4), Value16(4));
        Assert.Equal(2, root.Height);
        Assert.Equal(2, ReadInternalRoot(root).Keys.Count);

        // Insert 5 splits a leaf AND overflows the full root: the internal
        // middle key (3) moves UP into a new root, height 2 -> 3.
        root = InsertOk(tree, root, Key32(5), Value16(5));
        Assert.Equal(3, root.Height);
        rootNode = ReadInternalRoot(root);
        Assert.Single(rootNode.Keys);
        Assert.Equal(Key32(3), rootNode.Keys[0]);
        Assert.Equal(2, rootNode.Children.Count);

        Assert.Equal(5, root.EntryCount);
        for (int i = 1; i <= 5; i++)
            AssertFound(tree, root, i);
    }

    [Fact]
    public void Insert_SingleInsert_PropagatesSplitThroughTwoInternalLevels()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);

        // Ascending inserts 1..8 build a height-3 tree whose root is full
        // (keys [3,5]) and whose rightmost level-2 internal node is full, so
        // the next insert must split a leaf, then that internal node, then
        // the root — one insert propagating through two internal levels.
        var before = InsertRange(tree, null, Enumerable.Range(1, 8));
        Assert.Equal(3, before.Height);
        Assert.Equal(2, ReadInternalRoot(before).Keys.Count);

        var after = InsertOk(tree, before, Key32(9), Value16(9));

        // The cascade grew height by exactly 1 in a single insert.
        Assert.Equal(4, after.Height);
        Assert.Equal(9, after.EntryCount);

        // The new root holds exactly the single separator promoted from the
        // old root's middle key (5), with the split halves as its children.
        var afterNode = ReadInternalRoot(after);
        Assert.Single(afterNode.Keys);
        Assert.Equal(Key32(5), afterNode.Keys[0]);
        Assert.Equal(2, afterNode.Children.Count);

        // The split halves each keep one remaining separator of the old root
        // ([3] left of the promoted key, [7] promoted from the level-2 split).
        var leftChild = ReadInternalChild(tree, afterNode.Children[0]);
        var rightChild = ReadInternalChild(tree, afterNode.Children[1]);
        Assert.Equal(new[] { Key32(3) }, leftChild.Keys);
        Assert.Equal(new[] { Key32(7) }, rightChild.Keys);

        // Every key routes correctly through the promoted separators.
        for (int i = 1; i <= 9; i++)
            AssertFound(tree, after, i);

        // The pre-split root remains an intact height-3 snapshot without key 9.
        Assert.Equal(3, before.Height);
        for (int i = 1; i <= 8; i++)
            AssertFound(tree, before, i);
        AssertNotFound(tree, before, 9);
    }

    // ---- Upsert semantics ----

    [Fact]
    public void Insert_ExistingKey_ReplacesValueWithoutGrowingEntryCount()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 10));

        var updated = InsertOk(tree, root, Key32(7), Value16(999));

        Assert.Equal(root.EntryCount, updated.EntryCount);
        Assert.Equal(root.Height, updated.Height);
        AssertFound(tree, updated, 7, expectedValueSeed: 999);
        // The old version still sees the old value (COW snapshot).
        AssertFound(tree, root, 7, expectedValueSeed: 7);
    }

    // ---- Old roots remain readable snapshots ----

    [Fact]
    public void OldRoots_RemainFullyReadable_AfterFurtherInsertsAndSplits()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);

        var rootA = InsertRange(tree, null, Enumerable.Range(1, 20));
        var rootB = InsertRange(tree, rootA, Enumerable.Range(21, 40));
        var rootC = InsertOk(tree, rootB, Key32(5), Value16(555));

        // rootA: exactly keys 1..20, original values, even though many more
        // splits (including root splits) happened afterwards.
        Assert.Equal(20, rootA.EntryCount);
        for (int i = 1; i <= 20; i++)
            AssertFound(tree, rootA, i);
        AssertNotFound(tree, rootA, 21);
        AssertNotFound(tree, rootA, 60);

        // rootB: keys 1..60 with the pre-update value of key 5.
        Assert.Equal(60, rootB.EntryCount);
        for (int i = 1; i <= 60; i++)
            AssertFound(tree, rootB, i);
        AssertFound(tree, rootB, 5, expectedValueSeed: 5);

        // rootC sees the updated value.
        AssertFound(tree, rootC, 5, expectedValueSeed: 555);
    }

    // ---- Structural sharing ----

    [Fact]
    public void Insert_RewritesOnlyTouchedPath_UnchangedSubtreesShared()
    {
        var tree = CreateTree(maxLeafEntries: 4);
        // 5 ascending inserts -> height 2: left leaf [1,2], right leaf [3,4,5].
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));
        var rootNodeBefore = ReadInternalRoot(root);

        // Key 0 routes into the LEFT leaf and does not split it (2 -> 3 entries).
        var newRoot = InsertOk(tree, root, Key32(0), Value16(0));

        Assert.Equal(root.Height, newRoot.Height);
        var rootNodeAfter = ReadInternalRoot(newRoot);
        Assert.Equal(rootNodeBefore.Children.Count, rootNodeAfter.Children.Count);

        // The untouched right subtree's child record (reference + ChildHash) is
        // byte-identical — the subtree is shared, not rewritten.
        Assert.Equal(rootNodeBefore.Children[1], rootNodeAfter.Children[1]);
        // The touched left child was rewritten as a new block.
        Assert.NotEqual(rootNodeBefore.Children[0], rootNodeAfter.Children[0]);
    }

    // ---- Addressing modes and other index layouts ----

    [Fact]
    public void Insert_BlockLocationKind_UsesOffsetAddressedChildRecords()
    {
        var tree = new CowBTree(_store, BTreeIndexKind.BlockLocation, keySize: 16, leafValueSize: 16,
            maxLeafEntries: 4, maxInternalKeys: 3);
        Assert.Equal(BTreeChildAddressing.Offset, tree.Addressing);
        Assert.Equal(BTreeNodeCapacity.BlockLocationChildRecordSize, tree.ChildRecordSize); // 8 + 32 = 40

        BTreeRoot? root = null;
        for (int i = 1; i <= 30; i++)
            root = InsertOk(tree, root, Key(16, i), Value(16, i));

        Assert.True(root!.Height >= 2);
        Assert.Equal(30, root.EntryCount);
        for (int i = 1; i <= 30; i++)
        {
            var result = tree.TryGet(root, Key(16, i));
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
            Assert.True(result.Value.Found);
            Assert.Equal(Value(16, i), result.Value.Value);
        }

        // Offset-addressed reads must work without any BlockId resolver.
        var resolverlessTree = new CowBTree(new BTreeNodeStore(_manager, blockIdResolver: null),
            BTreeIndexKind.BlockLocation, keySize: 16, leafValueSize: 16,
            maxLeafEntries: 4, maxInternalKeys: 3);
        var lookup = resolverlessTree.TryGet(root, Key(16, 17));
        Assert.True(lookup.IsSuccess, lookup.IsFailure ? lookup.Error : null);
        Assert.True(lookup.Value.Found);
    }

    [Fact]
    public void Insert_DateKind_KeyOnlyEntries_Work()
    {
        // Date index: 24-byte composite key, empty value (spec Section 6.1).
        var tree = new CowBTree(_store, BTreeIndexKind.Date, keySize: 24, leafValueSize: 0,
            maxLeafEntries: 4, maxInternalKeys: 3);

        BTreeRoot? root = null;
        for (int i = 1; i <= 10; i++)
            root = InsertOk(tree, root, Key(24, i), Array.Empty<byte>());

        Assert.Equal(10, root!.EntryCount);
        Assert.True(root.Height >= 2);
        var result = tree.TryGet(root, Key(24, 6));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.True(result.Value.Found);
        Assert.Empty(result.Value.Value!);
    }

    [Fact]
    public void DefaultCapacities_MatchSpecRegistry()
    {
        var tree = new CowBTree(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16);

        // Spec Section 6.1: PrimaryEmail leaf 83 entries, internal 49 keys / 50 children.
        Assert.Equal(83, tree.MaxLeafEntries);
        Assert.Equal(49, tree.MaxInternalKeys);
        Assert.Equal(BTreeNodeCapacity.PrimaryEmailChildRecordSize, tree.ChildRecordSize);
    }

    // ---- Merkle verification and argument validation ----

    [Fact]
    public void TryGet_TamperedRootHash_FailsMerkleVerification()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 10));

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

        var result = tree.TryGet(tamperedRoot, Key32(1));
        Assert.True(result.IsFailure);
        Assert.Contains("Merkle", result.Error);
    }

    [Fact]
    public void Insert_WrongKeyOrValueWidth_Throws()
    {
        var tree = CreateTree();

        Assert.Throws<ArgumentException>(() => tree.Insert(null, new byte[5], new byte[16]));
        Assert.Throws<ArgumentException>(() => tree.Insert(null, new byte[32], new byte[3]));
    }

    [Fact]
    public void Constructor_RejectsDegenerateCapacities()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateTree(maxLeafEntries: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateTree(maxInternalKeys: 1));
    }

    // ------------------------------------------------------- InsertBatch (US-EMDB-104)

    private static BTreeLeafEntry Entry(int i) => new(Key32(i), Value16(i));

    private static BTreeRoot InsertBatchOk(CowBTree tree, BTreeRoot? root, IReadOnlyList<BTreeLeafEntry> entries)
    {
        var result = tree.InsertBatch(root, entries);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        return result.Value;
    }

    /// <summary>
    /// Full structural walk of one tree version over Merkle-verified reads: the
    /// same B+-tree invariants the model-stress harness checks — non-root
    /// occupancy minima, occupancy maxima, strict key ordering, Keys+1 ==
    /// Children — plus the leaf-entry total. Returns the counted entries.
    /// </summary>
    private long ValidateSubtree(BTreeRoot root)
    {
        ValidateNode(root.RootRef, root.Height, isRoot: true, out long count);
        return count;
    }

    private void ValidateNode(BTreeNodeRef nodeRef, int height, bool isRoot, out long counted)
    {
        var tree = CreateTree(); // only its Min/Max properties are used
        if (height <= 1)
        {
            var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
            var leaf = BTreeNodeSerializer.DeserializeLeaf(payload.Value);
            Assert.True(leaf.IsSuccess, leaf.IsFailure ? leaf.Error : null);
            var entries = leaf.Value.Entries;
            int minimum = isRoot ? 1 : tree.MinLeafEntries;
            Assert.True(entries.Count >= minimum, $"Leaf occupancy {entries.Count} below minimum {minimum} (isRoot={isRoot}).");
            Assert.True(entries.Count <= tree.MaxLeafEntries, $"Leaf occupancy {entries.Count} above maximum {tree.MaxLeafEntries}.");
            for (int i = 1; i < entries.Count; i++)
                Assert.True(entries[i - 1].Key.AsSpan().SequenceCompareTo(entries[i].Key) < 0, "Leaf keys must be strictly ascending.");
            counted = entries.Count;
            return;
        }

        var nodePayload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(nodePayload.IsSuccess, nodePayload.IsFailure ? nodePayload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(nodePayload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        var internalNode = node.Value;
        int minKeys = isRoot ? 1 : tree.MinInternalKeys;
        Assert.True(internalNode.Keys.Count >= minKeys, $"Internal occupancy {internalNode.Keys.Count} keys below minimum {minKeys} at height {height} (isRoot={isRoot}).");
        Assert.True(internalNode.Keys.Count <= tree.MaxInternalKeys, $"Internal occupancy {internalNode.Keys.Count} keys above maximum {tree.MaxInternalKeys}.");
        Assert.Equal(internalNode.Keys.Count + 1, internalNode.Children.Count);
        for (int i = 1; i < internalNode.Keys.Count; i++)
            Assert.True(internalNode.Keys[i - 1].AsSpan().SequenceCompareTo(internalNode.Keys[i]) < 0, "Internal routing keys must be strictly ascending.");

        counted = 0;
        foreach (var record in internalNode.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            ValidateNode(childRef.Value, height - 1, isRoot: false, out long childCount);
            counted += childCount;
        }
    }

    private static List<KeyValuePair<int, int>> ScanAll(CowBTree tree, BTreeRoot? root)
    {
        var found = new List<KeyValuePair<int, int>>();
        var scan = tree.Scan(root);
        while (true)
        {
            var step = scan.MoveNext();
            Assert.True(step.IsSuccess, step.IsFailure ? step.Error : null);
            if (!step.Value)
                break;
            found.Add(new KeyValuePair<int, int>(
                BinaryPrimitives.ReadInt32BigEndian(scan.Current.Key.AsSpan(28)),
                BinaryPrimitives.ReadInt32BigEndian(scan.Current.Value.AsSpan(12))));
        }
        return found;
    }

    [Fact]
    public void InsertBatch_BuildsFromEmpty_MatchesIndividualInserts_AndAmortizes()
    {
        // A sorted bulk build over an empty tree lands every entry and produces a
        // multi-level, invariant-satisfying tree — writing far fewer node blocks
        // than the same keys inserted one at a time (BTree_Index.md Section 4).
        const int n = 60;
        var entries = Enumerable.Range(0, n).Select(Entry).ToList();

        var tree = CreateTree();
        var root = InsertBatchOk(tree, null, entries);

        Assert.Equal(n, root.EntryCount);
        Assert.True(root.Height >= 3, $"Expected a genuinely multi-level tree, got height {root.Height}.");
        ValidateSubtree(root);
        var scanned = ScanAll(tree, root);
        Assert.Equal(Enumerable.Range(0, n).ToList(), scanned.Select(p => p.Key).ToList());
        Assert.Equal(Enumerable.Range(0, n).ToList(), scanned.Select(p => p.Value).ToList());

        long batchWrites = _offsetMap.Count;
        // Same keys, one at a time, into a fresh isolated tree: count its node writes.
        long individualWrites = CountWritesInFreshTree(t =>
        {
            BTreeRoot? r = null;
            foreach (var e in entries)
                r = InsertOk(t, r, e.Key, e.Value);
        });
        Assert.True(batchWrites < individualWrites,
            $"Batch wrote {batchWrites} node blocks; {n} individual inserts wrote {individualWrites}.");
        Assert.True(batchWrites < n, $"A single COW pass over {n} entries wrote {batchWrites} node blocks; expected fewer than {n}.");
    }

    /// <summary>
    /// The B+-tree's own routing rule (CowBTree.RouteChildIndex): descend the
    /// first child whose separator is strictly greater than the key, i.e. the
    /// count of routing keys ≤ the key. Kept in lockstep so the test computes
    /// the exact set of nodes an insert of <paramref name="key"/> would touch.
    /// </summary>
    private static int RouteChildIndex(IReadOnlyList<byte[]> routingKeys, byte[] key)
    {
        int index = 0;
        while (index < routingKeys.Count && routingKeys[index].AsSpan().SequenceCompareTo(key) <= 0)
            index++;
        return index;
    }

    private BTreeInternalNode ReadInternalNode(BTreeNodeRef nodeRef)
    {
        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        return node.Value;
    }

    /// <summary>
    /// Counts the DISTINCT nodes on the union of the given keys' root-to-leaf
    /// paths in <paramref name="root"/> — the exact "touched node" set a batch
    /// of those keys must rewrite when nothing splits. Nodes are identified by
    /// their (unique) COW block reference, so a node shared by several paths is
    /// counted once.
    /// </summary>
    private long CountDistinctTouchedNodes(CowBTree tree, BTreeRoot root, IEnumerable<int> keys)
    {
        var touched = new HashSet<string>();
        foreach (var i in keys)
        {
            var key = Key32(i);
            var nodeRef = root.RootRef;
            for (int height = root.Height; height > 1; height--)
            {
                touched.Add(Convert.ToHexString(nodeRef.Reference));
                var node = ReadInternalNode(nodeRef);
                int childIndex = RouteChildIndex(node.Keys, key);
                var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, node.Children[childIndex]);
                Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
                nodeRef = childRef.Value;
            }
            touched.Add(Convert.ToHexString(nodeRef.Reference)); // leaf
        }
        return touched.Count;
    }

    /// <summary>
    /// Node blocks written by applying <paramref name="upserts"/> ONE AT A TIME
    /// (each a full root-to-leaf COW path rewrite) to a fresh, isolated copy of
    /// the ascending 0..<paramref name="baseCount"/> tree — the per-entry cost
    /// the batch amortizes away. Returns only the writes attributable to the
    /// upserts, not the base build.
    /// </summary>
    private long CountLoopedUpsertWrites(int baseCount, IReadOnlyList<BTreeLeafEntry> upserts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-cowbtree-loop-{Guid.NewGuid():N}.emdb");
        var map = new RuntimeBlockOffsetMap();
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var manager = new BlockManager(stream, offsetMap: map, firstBlockOffset: 0, ownsStream: true);
        try
        {
            var store = new BTreeNodeStore(manager, map);
            var tree = new CowBTree(store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
                maxLeafEntries: 4, maxInternalKeys: 3);
            BTreeRoot? r = InsertRange(tree, null, Enumerable.Range(0, baseCount));
            long before = map.Count;
            foreach (var e in upserts)
                r = InsertOk(tree, r, e.Key, e.Value);
            return map.Count - before;
        }
        finally
        {
            manager.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void InsertBatch_RewritesEachTouchedNodeExactlyOnce_NotOncePerEntry()
    {
        // The acceptance criterion itself: "a batch of N sorted inserts rewrites
        // each touched node path ONCE, not per-entry" (BTree_Index.md Section 4).
        // Upserting EXISTING keys changes no occupancy, so nothing splits and the
        // set of rewritten nodes is EXACTLY the union of the batch keys' root-to-
        // leaf paths — making "each touched node exactly once" a direct equality.
        var tree = CreateTree();
        const int total = 120;
        var root = InsertRange(tree, null, Enumerable.Range(0, total));
        Assert.True(root.Height >= 3, $"Expected a genuinely multi-level tree, got height {root.Height}.");

        // A clustered run of existing keys: many share leaves and internal ancestors.
        var upsertKeys = Enumerable.Range(20, 60).ToArray();
        var batch = upsertKeys.Select(i => new BTreeLeafEntry(Key32(i), Value16(i + 500))).ToList();

        // Independently derive the exact touched-node set from the PRE-batch tree.
        long distinctTouchedNodes = CountDistinctTouchedNodes(tree, root, upsertKeys);

        long before = _offsetMap.Count;
        var newRoot = InsertBatchOk(tree, root, batch);
        long batchWrites = _offsetMap.Count - before;

        // No split, no new key: identical shape and entry count.
        Assert.Equal(root.Height, newRoot.Height);
        Assert.Equal(root.EntryCount, newRoot.EntryCount);

        // THE criterion: node blocks written == distinct touched nodes, i.e. each
        // touched leaf and internal-spine node is rewritten EXACTLY ONCE.
        Assert.Equal(distinctTouchedNodes, batchWrites);

        // Amortized, not per-entry: shared leaves/spine mean strictly fewer node
        // blocks than there are entries in the batch.
        Assert.True(batchWrites < batch.Count,
            $"Batch of {batch.Count} upserts rewrote {batchWrites} nodes; one COW pass must write fewer than one node per entry.");

        // The same upserts applied ONE AT A TIME each rewrite a full root-to-leaf
        // path — exactly Height nodes per entry, N × Height in total — which the
        // single amortized pass beats by a wide margin. This is the "not per-entry"
        // half of the criterion made quantitative.
        long individualWrites = CountLoopedUpsertWrites(total, batch);
        Assert.Equal(batch.Count * (long)root.Height, individualWrites);
        Assert.True(batchWrites < individualWrites,
            $"Batch wrote {batchWrites} nodes; {batch.Count} individual upserts wrote {individualWrites} (one path each).");

        // The upserted values are visible in the new version; the old root is an
        // untouched snapshot (COW).
        foreach (var i in upsertKeys)
            AssertFound(tree, newRoot, i, expectedValueSeed: i + 500);
        foreach (var i in upsertKeys)
            AssertFound(tree, root, i, expectedValueSeed: i);
    }

    private long CountWritesInFreshTree(Action<CowBTree> mutate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"emaildb-cowbtree-batch-{Guid.NewGuid():N}.emdb");
        var map = new RuntimeBlockOffsetMap();
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        var manager = new BlockManager(stream, offsetMap: map, firstBlockOffset: 0, ownsStream: true);
        try
        {
            var store = new BTreeNodeStore(manager, map);
            var tree = new CowBTree(store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
                maxLeafEntries: 4, maxInternalKeys: 3);
            mutate(tree);
            return map.Count;
        }
        finally
        {
            manager.Dispose();
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(20260705)]
    public void InsertBatch_RandomBatchesOverRandomTrees_MatchLoopedInserts(int seed)
    {
        // The safety-critical equivalence: applying a random sorted batch in one COW
        // pass must yield EXACTLY the key->value mapping (and a valid B+-tree) that
        // applying the same entries one-by-one produces, across pre-existing trees of
        // random shape, disjoint/overlapping keys, and repeated batches.
        var random = new Random(seed);
        var tree = CreateTree();

        // A reference model of the live mapping; two roots kept in lockstep.
        var model = new SortedDictionary<int, int>();
        BTreeRoot? batchRoot = null;
        BTreeRoot? loopRoot = null;
        int valueSeed = 1_000_000;

        for (int round = 0; round < 8; round++)
        {
            // Build a random sorted, unique batch. Mix fresh keys and upserts of live ones.
            int batchSize = random.Next(1, 40);
            var chosen = new SortedDictionary<int, int>();
            for (int i = 0; i < batchSize; i++)
            {
                bool upsertLive = model.Count > 0 && random.NextDouble() < 0.35;
                int key = upsertLive
                    ? model.Keys.ElementAt(random.Next(model.Count))
                    : random.Next(0, 500);
                chosen[key] = valueSeed++; // last write wins within the batch
            }
            var entries = chosen.Select(kv => new BTreeLeafEntry(Key32(kv.Key), Value16(kv.Value))).ToList();

            batchRoot = InsertBatchOk(tree, batchRoot, entries);
            foreach (var kv in chosen)
            {
                loopRoot = InsertOk(tree, loopRoot, Key32(kv.Key), Value16(kv.Value));
                model[kv.Key] = kv.Value;
            }

            // Same entry count and same full ordered mapping as the looped tree AND the model.
            Assert.Equal(model.Count, batchRoot.EntryCount);
            Assert.Equal(loopRoot!.EntryCount, batchRoot.EntryCount);
            var batchScan = ScanAll(tree, batchRoot);
            Assert.Equal(model.Select(kv => kv.Key).ToList(), batchScan.Select(p => p.Key).ToList());
            Assert.Equal(model.Select(kv => kv.Value).ToList(), batchScan.Select(p => p.Value).ToList());
            // Point lookups agree with the model for every live key.
            foreach (var kv in model)
                AssertFound(tree, batchRoot, kv.Key, kv.Value);
            // Structural invariants hold on the batch-built tree.
            long counted = 0;
            ValidateNode(batchRoot.RootRef, batchRoot.Height, isRoot: true, out counted);
            Assert.Equal(model.Count, counted);
        }
    }

    [Fact]
    public void InsertBatch_EmptyBatchOnExistingTree_ReturnsSameRoot()
    {
        var tree = CreateTree();
        var root = InsertBatchOk(tree, null, Enumerable.Range(0, 10).Select(Entry).ToList());
        var result = tree.InsertBatch(root, Array.Empty<BTreeLeafEntry>());
        Assert.True(result.IsSuccess);
        Assert.Same(root, result.Value);
    }

    [Fact]
    public void InsertBatch_RejectsUnsortedOrDuplicateOrWrongWidth()
    {
        var tree = CreateTree();
        Assert.Throws<ArgumentException>(() => tree.InsertBatch(null, new[] { Entry(2), Entry(1) }));
        Assert.Throws<ArgumentException>(() => tree.InsertBatch(null, new[] { Entry(1), Entry(1) }));
        Assert.Throws<ArgumentException>(() => tree.InsertBatch(null, new[] { new BTreeLeafEntry(new byte[5], new byte[16]) }));
        Assert.Throws<ArgumentException>(() => tree.InsertBatch(null, new[] { new BTreeLeafEntry(new byte[32], new byte[3]) }));
        Assert.Throws<ArgumentException>(() => tree.InsertBatch(null, Array.Empty<BTreeLeafEntry>()));
    }

    [Fact]
    public void InsertBatch_LeavesPreviousRootAReadableSnapshot()
    {
        var tree = CreateTree();
        var v1 = InsertBatchOk(tree, null, Enumerable.Range(0, 20).Select(Entry).ToList());
        var v2 = InsertBatchOk(tree, v1, Enumerable.Range(20, 20).Select(Entry).ToList());

        // v1 is untouched: it still sees exactly 0..19 and none of the delta.
        Assert.Equal(20, v1.EntryCount);
        Assert.Equal(40, v2.EntryCount);
        for (int i = 0; i < 20; i++)
            AssertFound(tree, v1, i);
        AssertNotFound(tree, v1, 30);
        for (int i = 0; i < 40; i++)
            AssertFound(tree, v2, i);
    }
}
