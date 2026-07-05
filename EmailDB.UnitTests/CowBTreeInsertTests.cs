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
}
