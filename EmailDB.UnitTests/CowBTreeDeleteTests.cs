using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 copy-on-write B+-tree delete path (US-EMDB-68-7,
/// docs/BTree_Index.md Section 4): COW path rewrite, leaf underflow
/// borrow/merge, internal-node underflow rebalancing with separator rotation
/// through the parent (required — v1 rebalanced only leaves), single-child
/// root collapse shrinking the height by exactly 1, and old roots remaining
/// fully readable snapshots after deletes. A structural walker verifies the
/// occupancy and ordering invariants of every node after each mutation.
/// </summary>
public class CowBTreeDeleteTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-del-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeDeleteTests()
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
    /// capacities so a handful of deletes forces underflow at every level.
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

    /// <summary>Deletes a key that must exist and returns the new root (null when the tree emptied).</summary>
    private static BTreeRoot? DeleteOk(CowBTree tree, BTreeRoot root, int i)
    {
        var result = tree.Delete(root, Key32(i));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.True(result.Value.Removed, $"Key {i} should have been present to delete.");
        return result.Value.Root;
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

    /// <summary>Reads and deserializes an internal child of an internal node.</summary>
    private BTreeInternalNode ReadInternalChild(CowBTree tree, BTreeInternalNode parent, int index)
    {
        var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, parent.Children[index]);
        Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
        var payload = _store.ReadVerified(childRef.Value, BTreeNodeKind.Internal);
        Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
        var node = BTreeNodeSerializer.DeserializeInternal(payload.Value);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        return node.Value;
    }

    // ---- Structural invariant walker ----

    /// <summary>
    /// Walks the whole tree version verifying the B+-tree invariants that
    /// delete rebalancing must maintain: every NON-ROOT leaf holds at least
    /// MinLeafEntries and every NON-ROOT internal node at least
    /// MinInternalKeys (this is the invariant leaf-only rebalancing breaks);
    /// no node exceeds its max; keys are strictly ascending; internal nodes
    /// hold exactly Keys + 1 children; and the leaf total matches
    /// <see cref="BTreeRoot.EntryCount"/>. All reads are Merkle-verified.
    /// </summary>
    private void AssertTreeInvariants(CowBTree tree, BTreeRoot? root, long expectedEntryCount)
    {
        if (root is null)
        {
            Assert.Equal(0, expectedEntryCount);
            return;
        }
        Assert.Equal(expectedEntryCount, root.EntryCount);
        long counted = CountAndValidateSubtree(tree, root.RootRef, root.Height, isRoot: true);
        Assert.Equal(expectedEntryCount, counted);
    }

    private long CountAndValidateSubtree(CowBTree tree, BTreeNodeRef nodeRef, int height, bool isRoot)
    {
        if (height <= 1)
        {
            var payload = _store.ReadVerified(nodeRef, BTreeNodeKind.Leaf);
            Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error : null);
            var leafResult = BTreeNodeSerializer.DeserializeLeaf(payload.Value);
            Assert.True(leafResult.IsSuccess, leafResult.IsFailure ? leafResult.Error : null);
            var entries = leafResult.Value.Entries;

            int minimum = isRoot ? 1 : tree.MinLeafEntries;
            Assert.True(entries.Count >= minimum,
                $"Leaf occupancy {entries.Count} below minimum {minimum} (isRoot={isRoot}).");
            Assert.True(entries.Count <= tree.MaxLeafEntries,
                $"Leaf occupancy {entries.Count} above maximum {tree.MaxLeafEntries}.");
            for (int i = 1; i < entries.Count; i++)
                Assert.True(entries[i - 1].Key.AsSpan().SequenceCompareTo(entries[i].Key) < 0,
                    "Leaf keys must be strictly ascending.");
            return entries.Count;
        }

        var nodePayload = _store.ReadVerified(nodeRef, BTreeNodeKind.Internal);
        Assert.True(nodePayload.IsSuccess, nodePayload.IsFailure ? nodePayload.Error : null);
        var nodeResult = BTreeNodeSerializer.DeserializeInternal(nodePayload.Value);
        Assert.True(nodeResult.IsSuccess, nodeResult.IsFailure ? nodeResult.Error : null);
        var node = nodeResult.Value;

        int minKeys = isRoot ? 1 : tree.MinInternalKeys;
        Assert.True(node.Keys.Count >= minKeys,
            $"Internal occupancy {node.Keys.Count} keys below minimum {minKeys} at height {height} (isRoot={isRoot}).");
        Assert.True(node.Keys.Count <= tree.MaxInternalKeys,
            $"Internal occupancy {node.Keys.Count} keys above maximum {tree.MaxInternalKeys}.");
        Assert.Equal(node.Keys.Count + 1, node.Children.Count);
        for (int i = 1; i < node.Keys.Count; i++)
            Assert.True(node.Keys[i - 1].AsSpan().SequenceCompareTo(node.Keys[i]) < 0,
                "Internal routing keys must be strictly ascending.");

        long total = 0;
        foreach (var record in node.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
            total += CountAndValidateSubtree(tree, childRef.Value, height - 1, isRoot: false);
        }
        return total;
    }

    // ---- Basics: missing keys, root leaf, emptying the tree ----

    [Fact]
    public void Delete_MissingKey_ReturnsNotRemoved_AndOriginalRoot()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 10));

        var result = tree.Delete(root, Key32(999));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        Assert.False(result.Value.Removed);
        // No path rewrite happened: the exact same root handle comes back.
        Assert.Same(root, result.Value.Root);
        AssertTreeInvariants(tree, root, 10);
    }

    [Fact]
    public void Delete_FromRootLeaf_NoRebalancingNeeded()
    {
        var tree = CreateTree(maxLeafEntries: 4);
        var root = InsertRange(tree, null, Enumerable.Range(1, 3));

        var newRoot = DeleteOk(tree, root, 2);

        Assert.NotNull(newRoot);
        Assert.Equal(1, newRoot!.Height);
        Assert.Equal(2, newRoot.EntryCount);
        AssertFound(tree, newRoot, 1);
        AssertNotFound(tree, newRoot, 2);
        AssertFound(tree, newRoot, 3);
        AssertTreeInvariants(tree, newRoot, 2);
    }

    [Fact]
    public void Delete_LastEntry_EmptiesTree_AndReinsertWorks()
    {
        var tree = CreateTree();
        var root = InsertOk(tree, null, Key32(1), Value16(1));

        var emptied = DeleteOk(tree, root, 1);
        Assert.Null(emptied);

        // Symmetric with Insert(null, ...): the tree restarts from empty.
        var reborn = InsertOk(tree, null, Key32(2), Value16(2));
        Assert.Equal(1, reborn.Height);
        Assert.Equal(1, reborn.EntryCount);
        AssertFound(tree, reborn, 2);
        // The pre-delete version still reads.
        AssertFound(tree, root, 1);
    }

    // ---- Leaf underflow: borrow and merge ----

    [Fact]
    public void Delete_LeafUnderflow_BorrowsFromRightSibling()
    {
        // maxLeaf 4 => MinLeafEntries 2. Inserting 1..5 splits into
        // leaves [1,2] | [3,4,5] under root separator 3.
        var tree = CreateTree(maxLeafEntries: 4);
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));
        Assert.Equal(2, root.Height);

        // Deleting 1 drops the left leaf to [2] (underflow); the right
        // sibling holds 3 > min and lends its first entry (3). The separator
        // becomes the right sibling's new first key, 4.
        var newRoot = DeleteOk(tree, root, 1)!;

        Assert.Equal(2, newRoot.Height);
        Assert.Equal(4, newRoot.EntryCount);
        var rootNode = ReadInternalRoot(newRoot);
        Assert.Single(rootNode.Keys);
        Assert.Equal(Key32(4), rootNode.Keys[0]);
        AssertNotFound(tree, newRoot, 1);
        for (int i = 2; i <= 5; i++)
            AssertFound(tree, newRoot, i);
        AssertTreeInvariants(tree, newRoot, 4);
    }

    [Fact]
    public void Delete_LeafUnderflow_BorrowsFromLeftSibling()
    {
        // Build leaves [0,1,2] | [3,4,5] (insert 1..5 splits, then 0 joins the
        // left leaf), separator 3.
        var tree = CreateTree(maxLeafEntries: 4);
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));
        root = InsertOk(tree, root, Key32(0), Value16(0));

        // Delete 4 (right leaf at min), then 5: the right leaf underflows to
        // [3]; only the LEFT sibling (3 entries > min) can lend. Its last
        // entry (2) moves over and becomes the new separator.
        root = DeleteOk(tree, root, 4)!;
        root = DeleteOk(tree, root, 5)!;

        Assert.Equal(2, root.Height);
        Assert.Equal(4, root.EntryCount);
        var rootNode = ReadInternalRoot(root);
        Assert.Single(rootNode.Keys);
        Assert.Equal(Key32(2), rootNode.Keys[0]);
        for (int i = 0; i <= 3; i++)
            AssertFound(tree, root, i);
        AssertNotFound(tree, root, 4);
        AssertNotFound(tree, root, 5);
        AssertTreeInvariants(tree, root, 4);
    }

    [Fact]
    public void Delete_LeafUnderflow_MergesAndCollapsesSingleChildRoot()
    {
        // Leaves [1,2] | [3,4,5]. Delete 5 then 4: the right leaf underflows
        // to [3] with the left sibling at exact minimum — no borrow possible,
        // so the leaves merge, the root loses its only separator, and the
        // single-child root collapses: height 2 -> 1.
        var tree = CreateTree(maxLeafEntries: 4);
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));
        Assert.Equal(2, root.Height);

        root = DeleteOk(tree, root, 5)!;
        Assert.Equal(2, root.Height);

        root = DeleteOk(tree, root, 4)!;
        Assert.Equal(1, root.Height);
        Assert.Equal(3, root.EntryCount);
        for (int i = 1; i <= 3; i++)
            AssertFound(tree, root, i);
        AssertTreeInvariants(tree, root, 3);
    }

    // ---- Internal-node underflow (multi-level trees) ----

    [Fact]
    public void Delete_AscendingDrain_RebalancesInternalNodes_AndCollapsesRootsStepwise()
    {
        // Fan-out 3 tree, 40 keys => height >= 3, so draining it MUST
        // rebalance internal nodes (borrow from right siblings on this
        // ascending pass) and collapse the root repeatedly. Invariants are
        // verified after EVERY delete — leaf-only rebalancing would leave
        // underfull internal nodes and fail the walker immediately.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 40));
        Assert.True(root.Height >= 3, $"Test needs a multi-level tree, got height {root.Height}.");

        BTreeRoot? current = root;
        int previousHeight = root.Height;
        for (int i = 1; i <= 40; i++)
        {
            current = DeleteOk(tree, current!, i);
            if (current is null)
            {
                Assert.Equal(40, i);
                break;
            }
            Assert.True(current.Height <= previousHeight, "Height must never grow on delete.");
            Assert.True(previousHeight - current.Height <= 1,
                $"Height dropped from {previousHeight} to {current.Height} on a single delete.");
            previousHeight = current.Height;
            Assert.Equal(40 - i, current.EntryCount);
            AssertTreeInvariants(tree, current, 40 - i);
            AssertNotFound(tree, current, i);
            AssertFound(tree, current, i + 1);
        }
        Assert.Null(current);
        // The original version is untouched by the entire drain.
        AssertTreeInvariants(tree, root, 40);
    }

    [Fact]
    public void Delete_DescendingDrain_RebalancesInternalNodes_ViaLeftSiblings()
    {
        // Mirror drain: deleting 40..1 underflows rightmost paths, forcing
        // internal borrows/merges with LEFT siblings (separator rotation the
        // other way).
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 40));
        Assert.True(root.Height >= 3, $"Test needs a multi-level tree, got height {root.Height}.");

        BTreeRoot? current = root;
        for (int i = 40; i >= 1; i--)
        {
            current = DeleteOk(tree, current!, i);
            AssertTreeInvariants(tree, current, i - 1);
            if (current is not null)
            {
                AssertNotFound(tree, current, i);
                AssertFound(tree, current, 1);
            }
        }
        Assert.Null(current);
        AssertTreeInvariants(tree, root, 40);
    }

    [Fact]
    public void Delete_InternalUnderflow_CollapsesRootByExactlyOneLevel()
    {
        // Deterministic single collapse of a multi-level tree: draining
        // ascending keys merges the internal nodes below the root until the
        // root has one child and collapses by EXACTLY one level —
        // internal-node rebalancing is the only path that gets there (the
        // tree is at least 3 levels tall, so the root's children are
        // internal nodes, not leaves).
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 13));
        int initialHeight = root.Height;
        Assert.True(initialHeight >= 3, $"Test needs a multi-level tree, got height {initialHeight}.");

        BTreeRoot? current = root;
        int deleted = 0;
        while (current!.Height == initialHeight)
        {
            deleted++;
            Assert.True(deleted < 13, $"Tree never collapsed below height {initialHeight}.");
            current = DeleteOk(tree, current, deleted);
            AssertTreeInvariants(tree, current, 13 - deleted);
        }
        Assert.Equal(initialHeight - 1, current.Height);
        for (int i = deleted + 1; i <= 13; i++)
            AssertFound(tree, current, i);
        for (int i = 1; i <= deleted; i++)
            AssertNotFound(tree, current, i);
    }

    // ---- Focused single-operation internal rebalance paths ----
    //
    // The drains above prove internal rebalancing holds in aggregate; these
    // four tests each isolate ONE internal-node rebalance path on a single
    // delete and pin the exact separator movement in the node contents.
    // Shapes are hand-traced for maxLeafEntries=2 / maxInternalKeys=2
    // (minimum occupancy 1 each): inserting 1..5 builds the height-3 tree
    //   root keys [3]
    //     A: keys [2], leaves [1] [2]
    //     B: keys [4], leaves [3] [4,5]

    [Fact]
    public void Delete_InternalUnderflow_BorrowsFromRightSibling_RotatingSeparatorThroughParent()
    {
        // Insert 6 on top of 1..5: B becomes keys [4,5] with leaves
        // [3] [4] [5,6] — richer than its minimum of 1 key.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 6));
        Assert.Equal(3, root.Height);

        // Delete 1: leaf [1] empties, merges with sibling [2], and A drops to
        // 0 keys. A cannot merge (B is above minimum) so it BORROWS from the
        // right: the root separator 3 rotates DOWN into A, B's first key 4 is
        // promoted UP to the root, and B's first child (leaf [3]) moves to A.
        var newRoot = DeleteOk(tree, root, 1)!;

        Assert.Equal(3, newRoot.Height); // borrow never changes the height
        Assert.Equal(5, newRoot.EntryCount);
        var rootNode = ReadInternalRoot(newRoot);
        Assert.Single(rootNode.Keys);
        Assert.Equal(Key32(4), rootNode.Keys[0]); // promoted from B

        var a = ReadInternalChild(tree, rootNode, 0);
        Assert.Single(a.Keys);
        Assert.Equal(Key32(3), a.Keys[0]); // the rotated-down separator
        var b = ReadInternalChild(tree, rootNode, 1);
        Assert.Single(b.Keys);
        Assert.Equal(Key32(5), b.Keys[0]);

        AssertNotFound(tree, newRoot, 1);
        for (int i = 2; i <= 6; i++)
            AssertFound(tree, newRoot, i);
        AssertTreeInvariants(tree, newRoot, 5);
        AssertTreeInvariants(tree, root, 6); // snapshot untouched
    }

    [Fact]
    public void Delete_InternalUnderflow_BorrowsFromLeftSibling_RotatingSeparatorThroughParent()
    {
        // Spaced keys 10..50 build the mirror base shape (root keys [30];
        // A: keys [20], leaves [10] [20]; B: keys [40], leaves [30] [40,50]);
        // 12 and 15 then grow the LEFT child to keys [12,20] with leaves
        // [10] [12,15] [20], so only A is above minimum.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, new[] { 10, 20, 30, 40, 50, 12, 15 });
        Assert.Equal(3, root.Height);

        // Delete 30 (leaf borrow inside B: leaves become [40] [50], keys [50])
        // then 40: leaf [40] empties, merges with [50], and B drops to 0 keys.
        // A is above minimum, so B borrows from the LEFT: the root separator
        // 30 rotates DOWN into B, A's last key 20 is promoted UP to the root,
        // and A's last child (leaf [20]) moves to B.
        var mid = DeleteOk(tree, root, 30)!;
        Assert.Equal(3, mid.Height);
        var newRoot = DeleteOk(tree, mid, 40)!;

        Assert.Equal(3, newRoot.Height);
        Assert.Equal(5, newRoot.EntryCount);
        var rootNode = ReadInternalRoot(newRoot);
        Assert.Single(rootNode.Keys);
        Assert.Equal(Key32(20), rootNode.Keys[0]); // promoted from A

        var a = ReadInternalChild(tree, rootNode, 0);
        Assert.Single(a.Keys);
        Assert.Equal(Key32(12), a.Keys[0]);
        var b = ReadInternalChild(tree, rootNode, 1);
        Assert.Single(b.Keys);
        Assert.Equal(Key32(30), b.Keys[0]); // the rotated-down separator

        foreach (int i in new[] { 10, 12, 15, 20, 50 })
            AssertFound(tree, newRoot, i);
        AssertNotFound(tree, newRoot, 30);
        AssertNotFound(tree, newRoot, 40);
        AssertTreeInvariants(tree, newRoot, 5);
        AssertTreeInvariants(tree, root, 7);
    }

    [Fact]
    public void Delete_InternalUnderflow_MergesPullingSeparatorDown_WithoutRootCollapse()
    {
        // Insert 1..7: the second internal split leaves the root with TWO
        // keys [3,5] over three internal children (A: keys [2]; B1: keys [4];
        // B2: keys [6]), all at exact minimum occupancy.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 7));
        Assert.Equal(3, root.Height);
        Assert.Equal(2, ReadInternalRoot(root).Keys.Count);

        // Delete 3: B1's leaf [3] empties, merges with [4], and B1 drops to 0
        // keys. Neither sibling can lend, so B1 MERGES with A and — unlike a
        // leaf merge, which drops the separator — the separator 3 is pulled
        // DOWN from the root into the merged node. The root keeps one key, so
        // the height must NOT change: this isolates internal-merge from the
        // root-collapse path.
        var newRoot = DeleteOk(tree, root, 3)!;

        Assert.Equal(3, newRoot.Height);
        Assert.Equal(6, newRoot.EntryCount);
        var rootNode = ReadInternalRoot(newRoot);
        Assert.Single(rootNode.Keys); // 2 keys -> 1 key, 3 children -> 2
        Assert.Equal(Key32(5), rootNode.Keys[0]);
        Assert.Equal(2, rootNode.Children.Count);

        var merged = ReadInternalChild(tree, rootNode, 0);
        Assert.Equal(2, merged.Keys.Count);
        Assert.Equal(Key32(2), merged.Keys[0]);
        Assert.Equal(Key32(3), merged.Keys[1]); // the pulled-down separator
        Assert.Equal(3, merged.Children.Count);

        AssertNotFound(tree, newRoot, 3);
        foreach (int i in new[] { 1, 2, 4, 5, 6, 7 })
            AssertFound(tree, newRoot, i);
        AssertTreeInvariants(tree, newRoot, 6);
        AssertTreeInvariants(tree, root, 7);
    }

    [Fact]
    public void Delete_InternalMerge_PullsSeparatorDown_AndCollapsesSingleChildRoot()
    {
        // The base 1..5 shape: both internal children at exact minimum and the
        // root holding its last separator.
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var root = InsertRange(tree, null, Enumerable.Range(1, 5));
        Assert.Equal(3, root.Height);

        // ONE delete cascades the full internal-merge + collapse path:
        // leaf [1] empties and merges with [2]; A drops to 0 keys and merges
        // with B, pulling the root's only separator 3 down; the root is left
        // with a single child and collapses, shrinking the height by exactly 1.
        var newRoot = DeleteOk(tree, root, 1)!;

        Assert.Equal(2, newRoot.Height); // exactly one level, in one delete
        Assert.Equal(4, newRoot.EntryCount);
        var collapsed = ReadInternalRoot(newRoot); // the merged node IS the new root
        Assert.Equal(2, collapsed.Keys.Count);
        Assert.Equal(Key32(3), collapsed.Keys[0]); // the pulled-down separator
        Assert.Equal(Key32(4), collapsed.Keys[1]);
        Assert.Equal(3, collapsed.Children.Count);

        AssertNotFound(tree, newRoot, 1);
        for (int i = 2; i <= 5; i++)
            AssertFound(tree, newRoot, i);
        AssertTreeInvariants(tree, newRoot, 4);
        AssertTreeInvariants(tree, root, 5);
    }

    // ---- Old roots remain readable snapshots ----

    [Fact]
    public void OldRoots_RemainFullyReadable_AfterDeletes()
    {
        var tree = CreateTree(maxLeafEntries: 2, maxInternalKeys: 2);
        var rootA = InsertRange(tree, null, Enumerable.Range(1, 30));

        // Capture every version while deleting 1..30.
        var versions = new List<BTreeRoot?> { rootA };
        BTreeRoot? current = rootA;
        for (int i = 1; i <= 30; i++)
        {
            current = DeleteOk(tree, current!, i);
            versions.Add(current);
        }
        Assert.Null(current);

        // rootA still reads all 30 original entries with original values, and
        // still passes the full structural walk (Merkle-verified), despite 30
        // rebalancing deletes (merges, borrows, root collapses) afterwards.
        AssertTreeInvariants(tree, rootA, 30);
        for (int i = 1; i <= 30; i++)
            AssertFound(tree, rootA, i);

        // Every intermediate version is a consistent snapshot: after deleting
        // 1..k it contains exactly the keys k+1..30.
        foreach (int k in new[] { 5, 15, 29 })
        {
            var version = versions[k]!;
            AssertTreeInvariants(tree, version, 30 - k);
            AssertNotFound(tree, version, k);
            AssertNotFound(tree, version, 1);
            for (int i = k + 1; i <= 30; i++)
                AssertFound(tree, version, i);
        }
    }

    // ---- Randomized stress against a reference model ----

    [Fact]
    public void Delete_RandomizedOrder_MatchesReferenceModel()
    {
        var tree = CreateTree(maxLeafEntries: 3, maxInternalKeys: 3);
        const int keyCount = 250;
        var random = new Random(20260704);

        var insertOrder = Enumerable.Range(1, keyCount).ToArray();
        random.Shuffle(insertOrder);
        var root = InsertRange(tree, null, insertOrder);
        Assert.True(root.Height >= 3);

        var deleteOrder = Enumerable.Range(1, keyCount).ToArray();
        random.Shuffle(deleteOrder);
        var alive = new HashSet<int>(insertOrder);

        BTreeRoot? current = root;
        for (int step = 0; step < keyCount; step++)
        {
            int key = deleteOrder[step];
            current = DeleteOk(tree, current!, key);
            alive.Remove(key);
            Assert.Equal(alive.Count, current?.EntryCount ?? 0);

            // Deleting an already-deleted key is a successful no-op.
            if (current is not null)
            {
                var again = tree.Delete(current, Key32(key));
                Assert.True(again.IsSuccess, again.IsFailure ? again.Error : null);
                Assert.False(again.Value.Removed);
                Assert.Same(current, again.Value.Root);
            }

            // Full structural + membership audit periodically and at the end.
            if (step % 25 == 24 || step == keyCount - 1)
            {
                AssertTreeInvariants(tree, current, alive.Count);
                if (current is not null)
                {
                    for (int i = 1; i <= keyCount; i++)
                    {
                        if (alive.Contains(i))
                            AssertFound(tree, current, i);
                        else
                            AssertNotFound(tree, current, i);
                    }
                }
            }
        }
        Assert.Null(current);
        // The pre-delete version survived 250 rebalancing deletes untouched.
        AssertTreeInvariants(tree, root, keyCount);
    }

    // ---- Other index layouts ----

    [Fact]
    public void Delete_DateKind_KeyOnlyEntries_RebalanceAndCollapse()
    {
        // Date index: 24-byte composite key, empty value (spec Section 6.1).
        var tree = new CowBTree(_store, BTreeIndexKind.Date, keySize: 24, leafValueSize: 0,
            maxLeafEntries: 2, maxInternalKeys: 2);

        BTreeRoot? root = null;
        for (int i = 1; i <= 20; i++)
            root = InsertOk(tree, root, Key(24, i), Array.Empty<byte>());
        Assert.True(root!.Height >= 3);

        for (int i = 1; i <= 19; i++)
        {
            var result = tree.Delete(root!, Key(24, i));
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
            Assert.True(result.Value.Removed);
            root = result.Value.Root;
            AssertTreeInvariants(tree, root, 20 - i);
        }
        Assert.Equal(1, root!.Height);
        var last = tree.TryGet(root, Key(24, 20));
        Assert.True(last.IsSuccess, last.IsFailure ? last.Error : null);
        Assert.True(last.Value.Found);
    }

    // ---- Argument validation ----

    [Fact]
    public void Delete_WrongKeyWidth_Throws_AndNullRootThrows()
    {
        var tree = CreateTree();
        var root = InsertRange(tree, null, Enumerable.Range(1, 3));

        Assert.Throws<ArgumentException>(() => tree.Delete(root, new byte[5]));
        Assert.Throws<ArgumentNullException>(() => tree.Delete(null!, new byte[32]));
    }
}
