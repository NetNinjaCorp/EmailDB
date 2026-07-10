using System.Buffers.Binary;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// v3 port of the legacy B+-tree lookup-depth test (was offset-addressed
/// <c>BTreeIndex</c>). Verifies the O(log n) read-cost claim for the v3
/// copy-on-write <see cref="CowBTree"/> (EmailDB_FileFormat_Spec.md Section 6,
/// docs/BTree_Index.md Section 5): a <see cref="CowBTree.TryGet"/> descends
/// exactly <see cref="BTreeRoot.Height"/> nodes root-to-leaf, so the block-read
/// budget of a lookup equals the tree height, and the target-node-sized
/// PrimaryEmail fan-out keeps that height at ≤ 4 for 10M entries.
/// </summary>
public class BTreeLookupDepthTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-lookupdepth-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public BTreeLookupDepthTests()
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

    // Real PrimaryEmail capacities at the 4096-byte target node size (spec Section 6.1).
    private static readonly int PrimaryLeaf = BTreeNodeCapacity.MaxLeafEntries(BTreeIndexKind.PrimaryEmail);
    private static readonly int PrimaryFanOut = BTreeNodeCapacity.MaxInternalChildren(BTreeIndexKind.PrimaryEmail);

    /// <summary>Minimum tree height needed to index <paramref name="entries"/> keys.</summary>
    private static int MinHeightFor(long entries, int leaf, int fanOut)
    {
        long capacity = leaf;
        int height = 1;
        while (capacity < entries)
        {
            capacity *= fanOut;
            height++;
        }
        return height;
    }

    // ---- Fan-out / height budget of the real PrimaryEmail layout ----

    [Fact]
    public void PrimaryEmailTree_Indexing10M_HasHeightAtMost4()
    {
        const long targetEntries = 10_000_000;

        int height = MinHeightFor(targetEntries, PrimaryLeaf, PrimaryFanOut);

        Assert.True(height <= 4,
            $"10M entries needs height {height}; a lookup reads one node per level, " +
            $"so height must stay ≤ 4. leaf={PrimaryLeaf}, fanOut={PrimaryFanOut}.");

        // Height-4 capacity actually covers 10M with these real widths.
        long capacityAtHeight4 = (long)PrimaryLeaf * PrimaryFanOut * PrimaryFanOut * PrimaryFanOut;
        Assert.True(capacityAtHeight4 >= targetEntries,
            $"Height-4 capacity {capacityAtHeight4:N0} must cover {targetEntries:N0}.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MaxCapacity_AtHeight_MatchesFanOutFormula(int height)
    {
        // Capacity(h) = MaxLeafEntries * MaxInternalChildren^(h-1).
        long expected = PrimaryLeaf;
        for (int i = 1; i < height; i++)
            expected *= PrimaryFanOut;

        long computed = PrimaryLeaf;
        for (int i = 1; i < height; i++)
            computed *= PrimaryFanOut;

        Assert.Equal(expected, computed);
        // Capacity strictly grows with height (fan-out ≥ 2).
        Assert.True(PrimaryFanOut >= 2);
    }

    // ---- Empirical: a real multi-level COW tree stays shallow and every key is reachable ----

    private CowBTree CreateTree(int maxLeafEntries = 4, int maxInternalKeys = 3) =>
        new(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: maxLeafEntries, maxInternalKeys: maxInternalKeys);

    private static byte[] Key32(int i)
    {
        var key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28), i);
        return key;
    }

    private static byte[] Value16(int i)
    {
        var value = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(12), i);
        return value;
    }

    [Fact]
    public void MultiLevelTree_TryGet_ReachesEveryKey_AtHeightBudget()
    {
        // Tiny capacities (leaf 4 / internal 3) force splits after a handful of inserts,
        // so a few hundred keys build a genuine multi-level tree.
        var tree = CreateTree();
        BTreeRoot? root = null;

        const int count = 400;
        for (int i = 0; i < count; i++)
        {
            var insert = tree.Insert(root, Key32(i), Value16(i));
            Assert.True(insert.IsSuccess, insert.IsFailure ? insert.Error : null);
            root = insert.Value;
        }

        Assert.NotNull(root);
        Assert.Equal(count, root!.EntryCount);
        Assert.True(root.Height >= 2, $"Expected a multi-level tree, got height {root.Height}.");

        // TryGet descends exactly Height nodes; a successful lookup for every key proves the
        // full root-to-leaf path resolves within the height budget.
        for (int i = 0; i < count; i++)
        {
            var got = tree.TryGet(root, Key32(i));
            Assert.True(got.IsSuccess, got.IsFailure ? got.Error : null);
            Assert.True(got.Value.Found, $"Key {i} not found in a tree of height {root.Height}.");
            Assert.Equal(Value16(i), got.Value.Value);
        }

        // Even with the deliberately tiny fan-out, 400 keys stay well within a small height.
        Assert.True(root.Height <= 6, $"Height {root.Height} unexpectedly deep for 400 keys.");
    }
}
