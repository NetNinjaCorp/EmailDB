using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that every B+-tree block contains a PrevChainHash field
/// linking to the previous block's NodeContentHash (copy-on-write chain),
/// or zeroed for genesis blocks (first node with no predecessor).
/// </summary>
public class BTreePrevChainHashTests : IDisposable
{
    private readonly string _tempDir;

    public BTreePrevChainHashTests()
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
    public async Task FirstLeaf_HasZeroedPrevChainHash_GenesisBlock()
    {
        // The very first leaf inserted into an empty tree should have an all-zero
        // PrevChainHash because there is no previous block version to chain to.
        var filePath = Path.Combine(_tempDir, "test_genesis.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 2, 3, 4);
        var result = await btreeIndex.InsertAsync(key, 4096, 100);
        Assert.True(result.IsSuccess, $"Insert failed: {result.Error}");

        // Read the leaf block
        var leaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(leaf);

        // PrevChainHash must be 32 bytes of zeros (genesis)
        Assert.Equal(32, leaf!.PrevChainHash.Length);
        Assert.All(leaf.PrevChainHash, b => Assert.Equal(0, b));

        // NodeContentHash must be non-zero (actual BLAKE3 hash)
        Assert.False(leaf.NodeContentHash.All(b => b == 0),
            "NodeContentHash should be non-zero for a valid leaf");
    }

    [Fact]
    public async Task UpdatedLeaf_PrevChainHash_MatchesPreviousNodeContentHash()
    {
        // When a leaf is updated (upsert), the new leaf's PrevChainHash should equal
        // the old leaf's NodeContentHash, forming the copy-on-write chain.
        var filePath = Path.Combine(_tempDir, "test_chain_update.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert first key
        var key = new EmailHashedID(10, 20, 30, 40);
        var r1 = await btreeIndex.InsertAsync(key, 1000, 1);
        Assert.True(r1.IsSuccess);

        // Capture the original leaf's NodeContentHash
        var originalLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(originalLeaf);
        var originalHash = (byte[])originalLeaf!.NodeContentHash.Clone();

        // Insert a second key (non-duplicate) — triggers copy-on-write of the leaf
        var key2 = new EmailHashedID(50, 60, 70, 80);
        var r2 = await btreeIndex.InsertAsync(key2, 2000, 2);
        Assert.True(r2.IsSuccess);

        // Read the new leaf version
        var updatedLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(updatedLeaf);

        // PrevChainHash of the updated leaf must match the original leaf's NodeContentHash
        Assert.Equal(originalHash, updatedLeaf!.PrevChainHash);
    }

    [Fact]
    public async Task UpsertedLeaf_PrevChainHash_MatchesPreviousNodeContentHash()
    {
        // When a duplicate key is upserted, the new leaf's PrevChainHash should chain
        // to the old leaf's NodeContentHash.
        var filePath = Path.Combine(_tempDir, "test_chain_upsert.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(42, 84, 126, 168);
        var r1 = await btreeIndex.InsertAsync(key, 1000, 1);
        Assert.True(r1.IsSuccess);

        var originalLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(originalLeaf);
        var originalHash = (byte[])originalLeaf!.NodeContentHash.Clone();

        // Upsert: same key, different value
        var r2 = await btreeIndex.InsertAsync(key, 9999, 999);
        Assert.True(r2.IsSuccess);

        var upsertedLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(upsertedLeaf);

        // Chain must link to previous version
        Assert.Equal(originalHash, upsertedLeaf!.PrevChainHash);
        // Content hash must differ (different payload)
        Assert.NotEqual(originalHash, upsertedLeaf.NodeContentHash);
    }

    [Fact]
    public async Task SplitLeaf_LeftChild_ChainsToOriginal_RightChild_IsGenesis()
    {
        // On a leaf split:
        // - Left child's PrevChainHash = original full leaf's NodeContentHash
        // - Right child's PrevChainHash = all zeros (new node, no predecessor)
        var filePath = Path.Combine(_tempDir, "test_chain_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Fill leaf to capacity
        for (int i = 0; i < BTreeLeafNode.MaxEntries; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Capture pre-split leaf's NodeContentHash
        var preSplitLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(preSplitLeaf);
        var preSplitHash = (byte[])preSplitLeaf!.NodeContentHash.Clone();

        // Trigger split
        var splitKey = new EmailHashedID(0, 0, 0, 1);
        var splitResult = await btreeIndex.InsertAsync(splitKey, 9999, 9999);
        Assert.True(splitResult.IsSuccess, $"Split insert failed: {splitResult.Error}");
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        // Read the internal root to find left and right child offsets
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        Assert.Equal(2, internalNode.ChildOffsets.Length);

        var leftBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[0]);
        var rightBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[1]);
        Assert.NotNull(leftBlock);
        Assert.NotNull(rightBlock);

        var leftLeaf = BTreeNodeSerializer.DeserializeLeaf(leftBlock!.Payload);
        var rightLeaf = BTreeNodeSerializer.DeserializeLeaf(rightBlock!.Payload);

        // Left child chains to the original leaf
        Assert.Equal(preSplitHash, leftLeaf.PrevChainHash);

        // Right child is a genesis block (all zeros)
        Assert.All(rightLeaf.PrevChainHash, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task InternalNode_PrevChainHash_ChainsCorrectlyOnUpdate()
    {
        // When an internal node is updated (copy-on-write due to child insert),
        // its PrevChainHash should match the previous version's NodeContentHash.
        var filePath = Path.Combine(_tempDir, "test_chain_internal.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build a 2-level tree
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        // Read the current internal root node and capture its hash
        var rootBlock1 = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock1);
        var internalBefore = BTreeNodeSerializer.DeserializeInternal(rootBlock1!.Payload);
        var internalBeforeHash = (byte[])internalBefore.NodeContentHash.Clone();

        // Insert another key — triggers copy-on-write of the internal root
        var newKey = new EmailHashedID(0, 0, 0, 99);
        var insertResult = await btreeIndex.InsertAsync(newKey, 77777, 77777);
        Assert.True(insertResult.IsSuccess, $"Insert failed: {insertResult.Error}");

        // Read the updated internal root
        var rootBlock2 = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock2);
        var internalAfter = BTreeNodeSerializer.DeserializeInternal(rootBlock2!.Payload);

        // PrevChainHash of the updated internal node must match the old version's hash
        Assert.Equal(internalBeforeHash, internalAfter.PrevChainHash);
    }

    [Fact]
    public async Task FirstInternalNode_HasZeroedPrevChainHash()
    {
        // The very first internal node created (when the root leaf splits) should
        // have a zeroed PrevChainHash because it has no predecessor.
        var filePath = Path.Combine(_tempDir, "test_first_internal.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        // Read the internal root
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // First internal node is a genesis block
        Assert.All(internalNode.PrevChainHash, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task MultipleInserts_AllLeafVersions_FormValidChain()
    {
        // Verify the chain across multiple sequential inserts into a single leaf.
        // Each insert produces a new leaf version where PrevChainHash = prior leaf's NodeContentHash.
        var filePath = Path.Combine(_tempDir, "test_chain_sequence.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        byte[]? previousHash = null;
        int insertCount = 10;

        for (int i = 0; i < insertCount; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var result = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");

            var leaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
            Assert.NotNull(leaf);

            if (i == 0)
            {
                // Genesis: PrevChainHash is all zeros
                Assert.All(leaf!.PrevChainHash, b => Assert.Equal(0, b));
            }
            else
            {
                // Chain: PrevChainHash matches previous leaf's NodeContentHash
                Assert.NotNull(previousHash);
                Assert.Equal(previousHash, leaf!.PrevChainHash);
            }

            previousHash = (byte[])leaf!.NodeContentHash.Clone();
        }
    }

    [Fact]
    public async Task PrevChainHash_Is32Bytes_OnAllBTreeBlocks()
    {
        // After building a multi-level tree, verify that every B+-tree block
        // (leaf and internal) has a 32-byte PrevChainHash field.
        var filePath = Path.Combine(_tempDir, "test_chain_all_blocks.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build a tree with enough inserts to create leaves and internals
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Scan all blocks in the file
        var locations = rawBlockManager.GetBlockLocations();
        int leafCount = 0;
        int internalCount = 0;

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                Assert.Equal(32, leaf.PrevChainHash.Length);
                Assert.Equal(32, leaf.NodeContentHash.Length);
                leafCount++;
            }
            else if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                Assert.Equal(32, internalNode.PrevChainHash.Length);
                Assert.Equal(32, internalNode.NodeContentHash.Length);
                internalCount++;
            }
        }

        Assert.True(leafCount > 0, "Should have at least one leaf block");
        Assert.True(internalCount > 0, "Should have at least one internal block");
    }

    [Fact]
    public async Task ActiveTree_AllCurrentNodes_HavePrevChainHash()
    {
        // Traverse the current (live) tree from root to all leaves and verify
        // every node has a PrevChainHash — either zeroed (genesis) or non-zero (chained).
        var filePath = Path.Combine(_tempDir, "test_chain_active_tree.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build a 3-level tree
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

        // BFS traversal of the active tree
        int nodesChecked = 0;
        var queue = new Queue<(long Offset, int Level)>();
        queue.Enqueue((btreeIndex.CurrentRoot.RootNodeBlockOffset, 1));

        while (queue.Count > 0)
        {
            var (offset, level) = queue.Dequeue();
            var block = await ReadBlockAtOffset(rawBlockManager, offset);
            Assert.NotNull(block);

            if (level < btreeIndex.CurrentRoot.TreeHeight)
            {
                // Internal node
                var internalNode = BTreeNodeSerializer.DeserializeInternal(block!.Payload);
                Assert.Equal(32, internalNode.PrevChainHash.Length);
                nodesChecked++;

                for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
                    queue.Enqueue((internalNode.ChildOffsets[i], level + 1));
            }
            else
            {
                // Leaf node
                var leaf = BTreeNodeSerializer.DeserializeLeaf(block!.Payload);
                Assert.Equal(32, leaf.PrevChainHash.Length);
                nodesChecked++;
            }
        }

        Assert.True(nodesChecked > 0, "Should have checked at least one node");
    }

    [Fact]
    public async Task PrevChainHash_RoundTrips_ThroughSerialization()
    {
        // Verify PrevChainHash survives write → read via RawBlockManager.
        var filePath = Path.Combine(_tempDir, "test_chain_roundtrip.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert two keys so the leaf has a non-zero PrevChainHash
        var r1 = await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        Assert.True(r1.IsSuccess);
        var r2 = await btreeIndex.InsertAsync(new EmailHashedID(2, 0, 0, 0), 200, 2);
        Assert.True(r2.IsSuccess);

        // Read the current leaf from disk
        var leaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(leaf);

        // PrevChainHash should be non-zero (chained to the first version)
        Assert.False(leaf!.PrevChainHash.All(b => b == 0),
            "PrevChainHash should be non-zero for a chained leaf");

        // Re-serialize and re-deserialize to verify round-trip
        var serialized = BTreeNodeSerializer.SerializeLeaf(leaf);
        var deserialized = BTreeNodeSerializer.DeserializeLeaf(serialized);

        Assert.Equal(leaf.PrevChainHash, deserialized.PrevChainHash);
        Assert.Equal(leaf.NodeContentHash, deserialized.NodeContentHash);
    }

    [Fact]
    public async Task DeletedLeaf_PrevChainHash_ChainsToOriginal()
    {
        // When a key is deleted, the new leaf version's PrevChainHash should
        // chain to the previous leaf's NodeContentHash.
        var filePath = Path.Combine(_tempDir, "test_chain_delete.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert several keys
        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Capture pre-delete leaf hash
        var preDeleteLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(preDeleteLeaf);
        var preDeleteHash = (byte[])preDeleteLeaf!.NodeContentHash.Clone();

        // Delete a key
        var deleteKey = new EmailHashedID(3, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(deleteKey);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Read the new leaf version
        var postDeleteLeaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(postDeleteLeaf);

        // PrevChainHash must chain to the pre-delete version
        Assert.Equal(preDeleteHash, postDeleteLeaf!.PrevChainHash);
    }

    #region Helpers

    private static async Task<BTreeLeafNode?> ReadLeafAtRootOffset(RawBlockManager rawBlockManager, BTreeIndex btreeIndex)
    {
        if (btreeIndex.CurrentRoot == null) return null;
        var block = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        return block != null ? BTreeNodeSerializer.DeserializeLeaf(block.Payload) : null;
    }

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
