using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that IndexRoot.RootNodeHash matches the actual BLAKE3 content hash
/// of the root node block. This is the top-level Merkle integrity property:
/// the IndexRoot stores the hash of the root node so that any tampering
/// with the root (or its subtree) is detectable by recomputing the hash.
/// </summary>
public class IndexRootHashMatchTests : IDisposable
{
    private readonly string _tempDir;

    public IndexRootHashMatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task SingleEntry_RootIsLeaf_RootNodeHash_MatchesLeafContentHash()
    {
        // A single-entry tree has height 1 where the root is a leaf node.
        // IndexRoot.RootNodeHash must equal the BLAKE3 content hash of that leaf.
        var filePath = Path.Combine(_tempDir, "root_hash_single.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 0, 0, 0);
        var result = await btreeIndex.InsertAsync(key, 100, 1);
        Assert.True(result.IsSuccess, $"Insert failed: {result.Error}");
        Assert.Equal((ushort)1, btreeIndex.CurrentRoot!.TreeHeight);

        // Read the root node (a leaf at this height)
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeLeafContentHash(leaf);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task MultipleEntries_RootIsLeaf_RootNodeHash_MatchesLeafContentHash()
    {
        // Fill a leaf partially (no split). Root is still a leaf.
        var filePath = Path.Combine(_tempDir, "root_hash_partial_leaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries / 2; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)1, btreeIndex.CurrentRoot!.TreeHeight);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeLeafContentHash(leaf);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task AfterFirstSplit_RootIsInternal_RootNodeHash_MatchesInternalContentHash()
    {
        // After the first leaf split, a 2-level tree is created. The root becomes
        // an internal node. IndexRoot.RootNodeHash must equal its content hash.
        var filePath = Path.Combine(_tempDir, "root_hash_after_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeInternalContentHash(internalNode);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task AfterAdditionalInserts_RootNodeHash_StaysConsistent()
    {
        // After additional inserts into a 2-level tree (copy-on-write rewrites root),
        // IndexRoot.RootNodeHash must still match the rewritten root's content hash.
        var filePath = Path.Combine(_tempDir, "root_hash_after_inserts.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        // Insert more keys to trigger copy-on-write of internal root
        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID(0, (ulong)(i + 1), 0, 0);
            var r = await btreeIndex.InsertAsync(key, (i + 1000) * 100, i + 1000);
            Assert.True(r.IsSuccess, $"Additional insert {i} failed: {r.Error}");
        }

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeInternalContentHash(internalNode);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task ThreeLevelTree_RootNodeHash_MatchesInternalContentHash()
    {
        // Build a 3-level tree and verify the IndexRoot.RootNodeHash matches the
        // actual content hash of the top-level internal root node.
        var filePath = Path.Combine(_tempDir, "root_hash_three_level.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        while (totalInserts < 3000)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(result.IsSuccess, $"Insert {totalInserts} failed: {result.Error}");
            totalInserts++;
            if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeInternalContentHash(internalNode);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task AfterDelete_RootNodeHash_StillMatchesRootContentHash()
    {
        // After deleting a key, the root is rewritten via copy-on-write.
        // IndexRoot.RootNodeHash must still match.
        var filePath = Path.Combine(_tempDir, "root_hash_after_delete.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var deleteKey = new EmailHashedID(5, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(deleteKey);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeInternalContentHash(internalNode);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task AfterUpsert_RootNodeHash_StillMatchesRootContentHash()
    {
        // After upserting an existing key, the root is rewritten.
        // IndexRoot.RootNodeHash must reflect the new root.
        var filePath = Path.Combine(_tempDir, "root_hash_after_upsert.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        var upsertKey = new EmailHashedID(3, 0, 0, 0);
        var upsertResult = await btreeIndex.InsertAsync(upsertKey, 99999, 99999);
        Assert.True(upsertResult.IsSuccess, $"Upsert failed: {upsertResult.Error}");

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var actualHash = BTreeHasher.ComputeInternalContentHash(internalNode);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
    }

    [Fact]
    public async Task RootNodeHash_EqualsRootNode_StoredNodeContentHash()
    {
        // The root node's own NodeContentHash field should also match IndexRoot.RootNodeHash.
        // This verifies consistency at both ends.
        var filePath = Path.Combine(_tempDir, "root_hash_equals_stored.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);

        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // IndexRoot.RootNodeHash must equal root node's own NodeContentHash
        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, internalNode.NodeContentHash);

        // And both must equal a freshly recomputed hash
        var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, recomputed);
    }

    [Fact]
    public async Task RootNodeHash_Is32Bytes_NonZero()
    {
        // IndexRoot.RootNodeHash must be a valid 32-byte BLAKE3 hash, not all zeros.
        var filePath = Path.Combine(_tempDir, "root_hash_format.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 0, 0, 0);
        var result = await btreeIndex.InsertAsync(key, 100, 1);
        Assert.True(result.IsSuccess, $"Insert failed: {result.Error}");

        Assert.Equal(32, btreeIndex.CurrentRoot!.RootNodeHash.Length);
        Assert.False(btreeIndex.CurrentRoot.RootNodeHash.All(b => b == 0),
            "RootNodeHash should not be all zeros");
    }

    [Fact]
    public async Task EveryInsert_RootNodeHash_AlwaysMatchesCurrentRoot()
    {
        // After every single insert, the IndexRoot.RootNodeHash must match the
        // actual root node's content hash. This ensures the invariant holds
        // continuously, not just at the end.
        var filePath = Path.Combine(_tempDir, "root_hash_every_insert.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");

            var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
            Assert.NotNull(rootBlock);

            byte[] actualHash;
            if (btreeIndex.CurrentRoot.TreeHeight == 1)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock!.Payload);
                actualHash = BTreeHasher.ComputeLeafContentHash(leaf);
            }
            else
            {
                var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
                actualHash = BTreeHasher.ComputeInternalContentHash(internalNode);
            }

            Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, actualHash);
        }
    }

    #region Helpers

    private static async Task<Block?> ReadBlockAtOffset(RawBlockManager rawBlockManager, long offset)
    {
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            if (kvp.Value.Position == offset)
            {
                var result = await rawBlockManager.ReadBlockAsync(kvp.Key);
                return result.IsSuccess ? result.Value : null;
            }
        }
        return null;
    }

    #endregion
}
