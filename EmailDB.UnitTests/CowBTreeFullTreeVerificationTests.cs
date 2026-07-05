using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 copy-on-write B+-tree full-tree verification mode
/// (US-EMDB-69-8, docs/BTree_Index.md Section 5; EmailDB_FileFormat_Spec.md
/// Section 6): <see cref="CowBTree.VerifyFullTree"/> walks EVERY node of a
/// given root, Merkle-verifying each against its parent's ChildHash (the root
/// against IndexRoot.RootHash), and on the first divergence reports that
/// node's root-to-node child-index path plus the contracted error. Used by
/// integrity audits and post-recovery checks.
/// </summary>
public class CowBTreeFullTreeVerificationTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-verify-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeFullTreeVerificationTests()
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

    private BTreeRoot InsertRange(CowBTree tree, BTreeRoot? root, IEnumerable<int> keys)
    {
        foreach (var i in keys)
        {
            var result = tree.Insert(root, Key32(i), Value16(i));
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
            root = result.Value;
        }
        return root!;
    }

    /// <summary>
    /// Independently counts every node in a tree version over Merkle-verified
    /// reads — the reference the full-tree walk's node count must match.
    /// </summary>
    private long CountNodes(CowBTree tree, BTreeNodeRef nodeRef, int height)
    {
        if (height <= 1)
        {
            var leaf = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            Assert.True(leaf.IsSuccess, leaf.IsFailure ? leaf.Error : null);
            return 1;
        }

        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);

        long total = 1;
        foreach (var record in node.Value.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            total += CountNodes(tree, childRef.Value, height - 1);
        }
        return total;
    }

    private BTreeInternalNode ReadInternal(CowBTree tree, BTreeNodeRef nodeRef)
    {
        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        return node.Value;
    }

    private BTreeNodeKind KindAt(CowBTree tree, BTreeRoot root, params int[] childPath)
    {
        // Height decreases by one per descended level; the node reached after
        // following `childPath` sits at height (root.Height - childPath.Length).
        return root.Height - childPath.Length <= 1 ? BTreeNodeKind.Leaf : BTreeNodeKind.Internal;
    }

    /// <summary>
    /// Returns a new root identical to <paramref name="root"/> except that the
    /// ChildHash of the child at <paramref name="childIndex"/> of the internal
    /// node reached by following <paramref name="parentPath"/> from the root is
    /// flipped. Every ancestor on the path is re-appended (COW) so the tree is
    /// structurally valid and every hash chains correctly EXCEPT that one
    /// poisoned ChildHash — which no longer matches the content it points at, so
    /// a reader descending to it fails Merkle verification.
    /// </summary>
    private BTreeRoot PoisonChildHash(CowBTree tree, BTreeRoot root, int[] parentPath, int childIndex)
    {
        var newRootRef = PoisonDescend(tree, root.RootRef, parentPath, 0, childIndex);
        return new BTreeRoot { RootRef = newRootRef, Height = root.Height, EntryCount = root.EntryCount };
    }

    private BTreeNodeRef PoisonDescend(
        CowBTree tree, BTreeNodeRef nodeRef, int[] parentPath, int depth, int childIndex)
    {
        var node = ReadInternal(tree, nodeRef);

        if (depth == parentPath.Length)
        {
            // This node is the parent whose child record we poison.
            var record = (byte[])node.Children[childIndex].Clone();
            record[^1] ^= 0xFF; // flip a byte of the trailing 32-byte ChildHash
            node.Children[childIndex] = record;
        }
        else
        {
            int next = parentPath[depth];
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, node.Children[next]);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            var newChild = PoisonDescend(tree, childRef.Value, parentPath, depth + 1, childIndex);
            node.Children[next] = newChild.ToChildRecord();
        }

        var write = _store.Append(BTreeNodeKind.Internal, BTreeNodeSerializer.SerializeInternal(node), tree.Addressing);
        Assert.True(write.IsSuccess, write.IsFailure ? write.Error : null);
        return write.Value;
    }

    // ---- Clean-tree pass and node count ----

    [Fact]
    public void VerifyFullTree_CleanTree_IsIntact_AndVisitsAllNodes()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 60));
        Assert.True(root.Height >= 3, $"Expected a multi-level tree, got height {root.Height}.");

        long expectedNodes = CountNodes(tree, root.RootRef, root.Height);

        var result = tree.VerifyFullTree(root);

        Assert.True(result.IsIntact, result.Error);
        Assert.Null(result.DivergentNodePath);
        Assert.Null(result.Error);
        Assert.Equal(expectedNodes, result.NodesVerified);
        Assert.True(result.NodesVerified > root.Height,
            "A multi-level tree must have more nodes than its height (fan-out > 1).");
    }

    /// <summary>
    /// Independently tallies total node count and leaf count of a tree version
    /// over Merkle-verified reads, recording every distinct internal level seen
    /// into <paramref name="internalHeights"/>. Together with the total this is
    /// the reference the full-tree walk must reproduce EXACTLY on a large, wide,
    /// multi-level tree — proving the walk visits every node, not merely as many
    /// nodes as it happens to count.
    /// </summary>
    private (long Total, long Leaves) Tally(
        CowBTree tree, BTreeNodeRef nodeRef, int height, HashSet<int> internalHeights)
    {
        if (height <= 1)
        {
            var leaf = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            Assert.True(leaf.IsSuccess, leaf.IsFailure ? leaf.Error : null);
            return (1, 1);
        }

        var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        internalHeights.Add(height);

        long total = 1, leaves = 0;
        foreach (var record in node.Value.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            var (t, l) = Tally(tree, childRef.Value, height - 1, internalHeights);
            total += t;
            leaves += l;
        }
        return (total, leaves);
    }

    [Fact]
    public void VerifyFullTree_LargeWideTree_VisitsEveryNode()
    {
        // A realistically sized, WIDE tree: hundreds of entries with real
        // fan-out, so every internal node has many child subtrees and there are
        // 2+ internal levels — the shape where a "skips a sibling subtree" walk
        // bug would hide but a minimal fan-out (2) tree would not expose.
        var tree = CreateTree(maxLeafEntries: 32, maxInternalKeys: 16);
        var root = InsertRange(tree, null, Enumerable.Range(1, 1000));
        Assert.True(root.Height >= 3, $"Expected 2+ internal levels, got height {root.Height}.");

        var internalHeights = new HashSet<int>();
        var (total, leaves) = Tally(tree, root.RootRef, root.Height, internalHeights);

        // Sanity-check the independent reference tally itself: the walk is only a
        // meaningful "walks all nodes" proof if it spans many leaves and 2+
        // internal levels. 1000 entries at <=32/leaf force at least 1000/32 leaves.
        Assert.True(leaves >= 1000 / 32, $"Expected many leaves, got {leaves}.");
        Assert.Equal(root.Height - 1, internalHeights.Count); // 2+ internal levels
        Assert.True(internalHeights.Count >= 2, $"Expected 2+ internal levels, got {internalHeights.Count}.");

        var result = tree.VerifyFullTree(root);

        Assert.True(result.IsIntact, result.Error);
        Assert.Null(result.DivergentNodePath);
        Assert.Null(result.Error);
        // The walk verified EXACTLY every node the independent enumeration found —
        // no leaf, sibling subtree, or internal level skipped.
        Assert.Equal(total, result.NodesVerified);
    }

    [Fact]
    public void VerifyFullTree_LargeWideTree_ColdCache_ReHashesEveryNode()
    {
        var tree = CreateTree(maxLeafEntries: 32, maxInternalKeys: 16);
        var root = InsertRange(tree, null, Enumerable.Range(1, 1000));
        long expected = CountNodes(tree, root.RootRef, root.Height);

        // Cold cache: every one of the hundreds of nodes is re-read from disk and
        // re-hashed during the audit, so a matching NodesVerified proves each node
        // was actually Merkle-verified, not counted off a warm cache.
        var coldStore = new BTreeNodeStore(_manager, _offsetMap, nodeCacheCapacity: 0);
        var coldTree = new CowBTree(coldStore, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 32, maxInternalKeys: 16);

        var result = coldTree.VerifyFullTree(root);

        Assert.True(result.IsIntact, result.Error);
        Assert.Equal(expected, result.NodesVerified);
    }

    [Fact]
    public void VerifyFullTree_ColdCache_StillVisitsAllNodes()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 40));
        long expectedNodes = CountNodes(tree, root.RootRef, root.Height);

        // A fresh store with caching disabled re-reads and re-hashes every node:
        // the audit result must be identical to the warm-cache path.
        var coldStore = new BTreeNodeStore(_manager, _offsetMap, nodeCacheCapacity: 0);
        var coldTree = new CowBTree(coldStore, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 2, maxInternalKeys: 2);

        var result = coldTree.VerifyFullTree(root);

        Assert.True(result.IsIntact, result.Error);
        Assert.Equal(expectedNodes, result.NodesVerified);
    }

    [Fact]
    public void VerifyFullTree_EmptyTree_IsIntact_ZeroNodes()
    {
        var tree = CreateTree();

        var result = tree.VerifyFullTree(null);

        Assert.True(result.IsIntact);
        Assert.Equal(0, result.NodesVerified);
        Assert.Null(result.DivergentNodePath);
        Assert.Null(result.Error);
    }

    [Fact]
    public void VerifyFullTree_SingleLeafRoot_IsIntact_OneNode()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 3));
        Assert.Equal(1, root.Height);

        var result = tree.VerifyFullTree(root);

        Assert.True(result.IsIntact, result.Error);
        Assert.Equal(1, result.NodesVerified);
    }

    // ---- Divergence detection and path reporting ----

    [Fact]
    public void VerifyFullTree_TamperedRootHash_ReportsRootPath()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 20));

        var tamperedHash = (byte[])root.RootRef.NodeHash.Clone();
        tamperedHash[0] ^= 0xFF;
        var tampered = new BTreeRoot
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

        var result = tree.VerifyFullTree(tampered);

        Assert.False(result.IsIntact);
        Assert.Equal("root", result.DivergentNodePath);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
        Assert.Equal(0, result.NodesVerified); // the root itself diverged, nothing verified
    }

    [Fact]
    public void VerifyFullTree_CorruptedLeaf_ReportsLeafPath()
    {
        // Height-2 tree: root internal node whose children are leaves.
        var tree = CreateTree(maxLeafEntries: 4);
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));
        Assert.Equal(2, root.Height);
        Assert.Equal(BTreeNodeKind.Leaf, KindAt(tree, root, 1));

        var poisoned = PoisonChildHash(tree, root, parentPath: Array.Empty<int>(), childIndex: 1);

        var result = tree.VerifyFullTree(poisoned);

        Assert.False(result.IsIntact);
        Assert.Equal("root/child[1]", result.DivergentNodePath);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
        // The root and the intact left leaf (child[0]) verified before the walk
        // reached the poisoned child[1].
        Assert.Equal(2, result.NodesVerified);
    }

    [Fact]
    public void VerifyFullTree_CorruptedInternalNode_ReportsInternalPath()
    {
        // Height-3 tree: the root's children are internal nodes.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 30));
        Assert.True(root.Height >= 3, $"Expected height >= 3, got {root.Height}.");
        Assert.Equal(BTreeNodeKind.Internal, KindAt(tree, root, 0));

        var poisoned = PoisonChildHash(tree, root, parentPath: Array.Empty<int>(), childIndex: 0);

        var result = tree.VerifyFullTree(poisoned);

        Assert.False(result.IsIntact);
        Assert.Equal("root/child[0]", result.DivergentNodePath);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
        // Only the root verified before the first (leftmost) child diverged.
        Assert.Equal(1, result.NodesVerified);
    }

    [Fact]
    public void VerifyFullTree_CorruptedDeepNode_ReportsMultiSegmentPath()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 30));
        Assert.True(root.Height >= 3);

        // Poison child[0] of the internal node reached via root/child[1]: the
        // reported path must be the full multi-segment chain to that node.
        var poisoned = PoisonChildHash(tree, root, parentPath: new[] { 1 }, childIndex: 0);

        var result = tree.VerifyFullTree(poisoned);

        Assert.False(result.IsIntact);
        Assert.Equal("root/child[1]/child[0]", result.DivergentNodePath);
        Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error), result.Error);
    }

    [Fact]
    public void VerifyFullTree_AfterDetectingDivergence_OriginalRootStillVerifiesClean()
    {
        // Poisoning is COW: it appends new nodes and never mutates the shared
        // originals, so the untouched original root is still fully intact — the
        // corruption contract's "fall back to the previous root" is sound.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 30));

        var poisoned = PoisonChildHash(tree, root, parentPath: new[] { 1 }, childIndex: 0);
        Assert.False(tree.VerifyFullTree(poisoned).IsIntact);

        var clean = tree.VerifyFullTree(root);
        Assert.True(clean.IsIntact, clean.Error);
    }
}
