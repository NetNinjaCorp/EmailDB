using System.Buffers.Binary;
using Blake3;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Write-path Merkle hash wiring for the v3 copy-on-write B+-tree (US-EMDB-69-6,
/// EmailDB_FileFormat_Spec.md Section 6, docs/BTree_Index.md Section 5). The
/// three write-path deliverables are verified end-to-end against independently
/// recomputed hashes (NOT via <see cref="BTreeNodeStore.ReadVerified"/>, which
/// would be circular — it checks the same embedded hash):
///
/// 1. NodeContentHash on serialize: BLAKE3-256 over the node's full serialized
///    payload (header + body).
/// 2. ChildHash embedded in every parent child record equals the referenced
///    child node's NodeContentHash.
/// 3. RootHash (<see cref="BTreeRoot.RootHash"/>, the IndexRoot.RootHash the
///    flush layer persists) equals the root node's NodeContentHash.
///
/// Every internal node in a multi-level tree is walked so the invariant holds
/// at every level, for both BlockId- (PrimaryEmail) and offset-addressed
/// (BlockLocation) trees.
/// </summary>
public class CowBTreeWritePathHashTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-cowbtree-hash-{Guid.NewGuid():N}.emdb");

    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;
    private readonly BTreeNodeStore _store;

    public CowBTreeWritePathHashTests()
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

    private static byte[] Key(int width, int i)
    {
        var key = new byte[width];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(width - 4), i);
        return key;
    }

    /// <summary>
    /// Reads a node's raw serialized payload by resolving its reference to a
    /// file offset directly — bypassing <see cref="BTreeNodeStore.ReadVerified"/>
    /// so the recomputed hash is independent of the embedded ChildHash.
    /// </summary>
    private byte[] ReadPayloadByRef(BTreeNodeRef nodeRef)
    {
        long offset;
        if (nodeRef.Addressing == BTreeChildAddressing.Offset)
        {
            offset = BinaryPrimitives.ReadInt64LittleEndian(nodeRef.Reference);
        }
        else
        {
            Assert.True(_offsetMap.TryGetLocation(nodeRef.Reference, out var location) && location is not null,
                "BlockId reference did not resolve to a location.");
            offset = location!.Offset;
        }

        var block = _manager.ReadDecompressed(offset);
        Assert.True(block.IsSuccess, block.IsFailure ? block.Error : null);
        return block.Value.Payload;
    }

    /// <summary>
    /// Asserts every child record under <paramref name="nodeRef"/> embeds the
    /// referenced child's true NodeContentHash, recursively to the leaves.
    /// <paramref name="height"/> counts down to 1 at the leaves.
    /// </summary>
    private void AssertSubtreeHashesWired(CowBTree tree, BTreeNodeRef nodeRef, int height)
    {
        var payload = ReadPayloadByRef(nodeRef);

        // The reference's own hash must be this node's real content hash.
        Assert.Equal(BTreeNodeSerializer.ComputeNodeContentHash(payload), nodeRef.NodeHash);

        if (height <= 1)
            return;

        var node = BTreeNodeSerializer.DeserializeInternal(payload);
        Assert.True(node.IsSuccess, node.IsFailure ? node.Error : null);

        foreach (var childRecord in node.Value.Children)
        {
            var childRef = BTreeNodeRef.FromChildRecord(tree.Addressing, childRecord);
            Assert.True(childRef.IsSuccess, childRef.IsFailure ? childRef.Error : null);

            // Independent recompute: the embedded ChildHash must equal the
            // child node's BLAKE3-256, proving the write path wired it.
            var childPayload = ReadPayloadByRef(childRef.Value);
            var expected = Blake3.Hasher.Hash(childPayload).AsSpan().ToArray();
            Assert.Equal(expected, childRef.Value.NodeHash);

            AssertSubtreeHashesWired(tree, childRef.Value, height - 1);
        }
    }

    [Fact]
    public void Insert_BlockIdTree_EveryChildHashAndRootHashWired()
    {
        var tree = new CowBTree(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 3, maxInternalKeys: 3);

        BTreeRoot? root = null;
        for (int i = 1; i <= 100; i++)
            root = tree.Insert(root, Key(32, i), Key(16, i)).Value;

        Assert.True(root!.Height >= 3, $"Expected a multi-level tree, got height {root.Height}.");

        // RootHash (== IndexRoot.RootHash) equals the root node's content hash.
        var rootPayload = ReadPayloadByRef(root.RootRef);
        Assert.Equal(BTreeNodeSerializer.ComputeNodeContentHash(rootPayload), root.RootHash);

        // Every child record at every level embeds the true child hash.
        AssertSubtreeHashesWired(tree, root.RootRef, root.Height);
    }

    [Fact]
    public void Insert_OffsetAddressedTree_EveryChildHashAndRootHashWired()
    {
        var tree = new CowBTree(_store, BTreeIndexKind.BlockLocation, keySize: 16, leafValueSize: 16,
            maxLeafEntries: 3, maxInternalKeys: 3);
        Assert.Equal(BTreeChildAddressing.Offset, tree.Addressing);

        BTreeRoot? root = null;
        for (int i = 1; i <= 100; i++)
            root = tree.Insert(root, Key(16, i), Key(16, i)).Value;

        Assert.True(root!.Height >= 3, $"Expected a multi-level tree, got height {root.Height}.");

        var rootPayload = ReadPayloadByRef(root.RootRef);
        Assert.Equal(BTreeNodeSerializer.ComputeNodeContentHash(rootPayload), root.RootHash);

        AssertSubtreeHashesWired(tree, root.RootRef, root.Height);
    }

    [Fact]
    public void Delete_RewrittenPath_KeepsEveryChildHashAndRootHashWired()
    {
        var tree = new CowBTree(_store, BTreeIndexKind.PrimaryEmail, keySize: 32, leafValueSize: 16,
            maxLeafEntries: 3, maxInternalKeys: 3);

        BTreeRoot? root = null;
        for (int i = 1; i <= 100; i++)
            root = tree.Insert(root, Key(32, i), Key(16, i)).Value;

        // Delete half the keys (forces merges/borrows that rewrite the path).
        for (int i = 1; i <= 100; i += 2)
        {
            var deleted = tree.Delete(root!, Key(32, i));
            Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error : null);
            Assert.True(deleted.Value.Removed);
            root = deleted.Value.Root;
        }

        Assert.NotNull(root);
        var rootPayload = ReadPayloadByRef(root!.RootRef);
        Assert.Equal(BTreeNodeSerializer.ComputeNodeContentHash(rootPayload), root.RootHash);
        AssertSubtreeHashesWired(tree, root.RootRef, root.Height);
    }
}
