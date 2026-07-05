using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Direct coverage for the US-EMDB-69 acceptance criterion "Any single bit flip
/// in any node fails the affected lookups with the contracted error"
/// (EmailDB_FileFormat_Spec.md Section 6, docs/BTree_Index.md Section 5).
///
/// Where <see cref="CowBTreeReadVerificationTests"/> flips a single fixed byte of
/// one child record (and the expected hash), these tests take the strong reading
/// literally: for a representative node at EVERY tree level (root, a mid-level
/// internal node, a leaf) they SWEEP every bit position across the node's whole
/// stored payload — the 12-byte node header, routing keys / leaf keys, and leaf
/// values / internal child records — and assert that each single-bit flip fails
/// the lookups that traverse the node with the contracted
/// <see cref="BTreeNodeStore.MerkleVerificationErrorCode"/> error, while lookups
/// whose path avoids the corrupted node still succeed.
///
/// Each flip is applied to the node's real serialized payload, which is then
/// re-appended as a fresh, VALID block (correct block checksum) whose parent (or
/// IndexRoot.RootHash for the root) still carries the original ChildHash. That
/// isolates Merkle path verification as the mechanism under test: the block
/// checksum passes, so only the ChildHash/RootHash comparison can catch the
/// tamper.
/// </summary>
public class CowBTreeBitFlipVerificationTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-bitflip-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeBitFlipVerificationTests()
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

    private const byte KeySize = 32;
    private const ushort ValueSize = 16;

    private static byte[] Key(int i)
    {
        var key = new byte[KeySize];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(KeySize - 4), i);
        return key;
    }

    private CowBTree NewTree() => new(
        _store, BTreeIndexKind.PrimaryEmail, KeySize, ValueSize,
        maxLeafEntries: 3, maxInternalKeys: 3);

    private BTreeRoot BuildTree(CowBTree tree, int count, int minHeight)
    {
        BTreeRoot? root = null;
        for (int i = 1; i <= count; i++)
        {
            var inserted = tree.Insert(root, Key(i), new byte[ValueSize]);
            Assert.True(inserted.IsSuccess, inserted.IsFailure ? inserted.Error : null);
            root = inserted.Value;
        }
        Assert.NotNull(root);
        Assert.True(root!.Height >= minHeight, $"Expected height >= {minHeight}, got {root.Height}.");
        return root;
    }

    // ---------------------------------------------------------------- helpers

    private byte[] ReadVerifiedPayload(BTreeNodeRef nodeRef, BTreeNodeKind kind)
    {
        var result = _store.ReadVerified(nodeRef, kind);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        return result.Value;
    }

    private BTreeInternalNode ReadInternal(BTreeNodeRef nodeRef)
    {
        var node = BTreeNodeSerializer.DeserializeInternal(ReadVerifiedPayload(nodeRef, BTreeNodeKind.Internal));
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);
        return node.Value;
    }

    private BTreeNodeRef FromChildRecord(CowBTree tree, byte[] record)
    {
        var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, record);
        Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);
        return childRef.Value;
    }

    /// <summary>Descends the ORIGINAL tree to the node reached by <paramref name="path"/>.</summary>
    private (BTreeNodeRef Ref, BTreeNodeKind Kind) ResolveTarget(CowBTree tree, BTreeRoot root, int[] path)
    {
        var nodeRef = root.RootRef;
        int height = root.Height;
        foreach (var idx in path)
        {
            nodeRef = FromChildRecord(tree, ReadInternal(nodeRef).Children[idx]);
            height--;
        }
        return (nodeRef, height <= 1 ? BTreeNodeKind.Leaf : BTreeNodeKind.Internal);
    }

    /// <summary>
    /// Re-appends the target node with its <paramref name="bitPosition"/>-th
    /// payload bit flipped as a fresh VALID block, and returns a reference to it
    /// carrying the ORIGINAL expected hash (<paramref name="keepHash"/>). The
    /// block checksum is correct, so only Merkle verification can catch the flip.
    /// </summary>
    private BTreeNodeRef ReappendFlipped(
        CowBTree tree, BTreeNodeRef nodeRef, BTreeNodeKind kind, int bitPosition, byte[] keepHash)
    {
        var tampered = (byte[])ReadVerifiedPayload(nodeRef, kind).Clone();
        tampered[bitPosition >> 3] ^= (byte)(1 << (bitPosition & 7));

        var write = _store.Append(kind, tampered, tree.Addressing);
        Assert.True(write.IsSuccess, write.IsFailure ? write.Error : null);
        return new BTreeNodeRef
        {
            Addressing = write.Value.Addressing,
            Reference = write.Value.Reference,
            NodeHash = keepHash, // parent's original ChildHash — now mismatches the flipped payload
        };
    }

    /// <summary>
    /// Produces a COW tree version identical to <paramref name="root"/> except
    /// that the node reached by <paramref name="path"/> has one payload bit
    /// flipped, while its parent's ChildHash (or IndexRoot.RootHash for the root)
    /// is preserved so a reader that traverses it fails Merkle verification.
    /// </summary>
    private BTreeRoot TamperNodePayload(CowBTree tree, BTreeRoot root, int[] path, int bitPosition)
    {
        if (path.Length == 0)
        {
            var kind = root.Height <= 1 ? BTreeNodeKind.Leaf : BTreeNodeKind.Internal;
            var tampered = ReappendFlipped(tree, root.RootRef, kind, bitPosition, root.RootRef.NodeHash);
            return new BTreeRoot { RootRef = tampered, Height = root.Height, EntryCount = root.EntryCount };
        }

        var newRootRef = TamperDescend(tree, root.Height, root.RootRef, path, 0, bitPosition);
        return new BTreeRoot { RootRef = newRootRef, Height = root.Height, EntryCount = root.EntryCount };
    }

    private BTreeNodeRef TamperDescend(
        CowBTree tree, int nodeHeight, BTreeNodeRef nodeRef, int[] path, int depth, int bitPosition)
    {
        var node = ReadInternal(nodeRef);
        int idx = path[depth];
        var childRef = FromChildRecord(tree, node.Children[idx]);

        if (depth == path.Length - 1)
        {
            // The child at idx is the target: flip its payload, keep this node's ChildHash.
            var childKind = nodeHeight - 1 <= 1 ? BTreeNodeKind.Leaf : BTreeNodeKind.Internal;
            var tamperedChild = ReappendFlipped(tree, childRef, childKind, bitPosition, childRef.NodeHash);
            node.Children[idx] = tamperedChild.ToChildRecord();
        }
        else
        {
            var newChild = TamperDescend(tree, nodeHeight - 1, childRef, path, depth + 1, bitPosition);
            node.Children[idx] = newChild.ToChildRecord();
        }

        // Re-append this rebuilt ancestor as a valid block; its own hash is correct.
        var write = _store.Append(BTreeNodeKind.Internal, BTreeNodeSerializer.SerializeInternal(node), tree.Addressing);
        Assert.True(write.IsSuccess, write.IsFailure ? write.Error : null);
        return write.Value;
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public void TryGet_AnySingleBitFlipInAnyNode_FailsAffectedLookupWithContractedMerkleError()
    {
        var tree = NewTree();
        var root = BuildTree(tree, 100, minHeight: 3);

        // Leftmost path so the smallest key traverses every level; a target at the
        // root, one interior level, and the leaf covers header/keys/values and
        // header/keys/child-records across the whole node format.
        var levels = new (string Name, int[] Path)[]
        {
            ("root", Array.Empty<int>()),
            ("internal", new[] { 0 }),
            ("leaf", Enumerable.Repeat(0, root.Height - 1).ToArray()),
        };

        var affectedKey = Key(1); // routes down the leftmost (tampered) path

        foreach (var (name, path) in levels)
        {
            var (targetRef, targetKind) = ResolveTarget(tree, root, path);
            int bitCount = ReadVerifiedPayload(targetRef, targetKind).Length * 8;
            Assert.True(bitCount > BTreeNodeSerializer.NodeHeaderSize * 8,
                $"{name}: payload should span more than the header ({bitCount} bits).");

            for (int bit = 0; bit < bitCount; bit++)
            {
                var tampered = TamperNodePayload(tree, root, path, bit);
                var result = tree.TryGet(tampered, affectedKey);

                Assert.True(result.IsFailure, $"{name} node, bit {bit}: expected the lookup to FAIL.");
                Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error),
                    $"{name} node, bit {bit}: expected the contracted Merkle error, got: {result.Error}");
            }
        }
    }

    [Fact]
    public void TryGet_SingleBitFlipInOneSubtree_LeavesUnrelatedLookupsIntact()
    {
        var tree = NewTree();
        var root = BuildTree(tree, 100, minHeight: 3);

        var affectedKey = Key(1);     // leftmost subtree — the one we corrupt
        var unaffectedKey = Key(100); // rightmost subtree — shares no tampered node

        foreach (var path in new[] { new[] { 0 }, Enumerable.Repeat(0, root.Height - 1).ToArray() })
        {
            var (targetRef, targetKind) = ResolveTarget(tree, root, path);
            int midBit = ReadVerifiedPayload(targetRef, targetKind).Length * 8 / 2;

            var tampered = TamperNodePayload(tree, root, path, midBit);

            var affected = tree.TryGet(tampered, affectedKey);
            Assert.True(affected.IsFailure);
            Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(affected.Error), affected.Error);

            var unaffected = tree.TryGet(tampered, unaffectedKey);
            Assert.True(unaffected.IsSuccess, unaffected.IsFailure ? unaffected.Error : null);
            Assert.True(unaffected.Value.Found);
        }
    }

    [Fact]
    public void TryGet_SingleBitFlipInRoot_FailsEveryLookup()
    {
        // The root is on every lookup's path, so a single flipped root-payload bit
        // must fail lookups across the whole key range — none can route around it.
        var tree = NewTree();
        var root = BuildTree(tree, 100, minHeight: 3);

        int midBit = ReadVerifiedPayload(root.RootRef, BTreeNodeKind.Internal).Length * 8 / 2;
        var tampered = TamperNodePayload(tree, root, Array.Empty<int>(), midBit);

        foreach (var i in new[] { 1, 25, 50, 75, 100 })
        {
            var result = tree.TryGet(tampered, Key(i));
            Assert.True(result.IsFailure, $"key {i}: expected failure through the corrupt root.");
            Assert.True(BTreeNodeStore.IsMerkleVerificationFailure(result.Error),
                $"key {i}: expected the contracted Merkle error, got: {result.Error}");
        }
    }
}
