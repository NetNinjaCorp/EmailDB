using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that internal node ChildHashes match the actual BLAKE3 content hashes
/// of the referenced child nodes. This is the Merkle integrity property: each internal
/// node stores the content hash of every child, so tampering with any child is
/// detectable by recomputing its hash and comparing against the parent's stored value.
/// </summary>
public class BTreeChildHashVerificationTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeChildHashVerificationTests()
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
    public async Task AfterFirstSplit_InternalRoot_ChildHashes_MatchLeafContentHashes()
    {
        // After the first leaf split, a 2-level tree is created. The internal root
        // should have ChildHashes that exactly match the BLAKE3 content hash of each
        // child leaf node.
        var filePath = Path.Combine(_tempDir, "child_hash_first_split.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Fill leaf to capacity, then trigger split
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

        // Verify each child hash matches the actual child's content hash
        for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
            var actualHash = BTreeHasher.ComputeLeafContentHash(childLeaf);

            Assert.Equal(internalNode.ChildHashes[i], actualHash);
        }
    }

    [Fact]
    public async Task AfterMultipleInserts_InternalRoot_ChildHashes_StayConsistent()
    {
        // After additional inserts into a 2-level tree (no further splits), the
        // internal root is rewritten via copy-on-write. Its ChildHashes must still
        // match the current child leaf content hashes.
        var filePath = Path.Combine(_tempDir, "child_hash_after_inserts.emdb");
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

        // Insert more keys (into existing leaves, triggering copy-on-write of internal root)
        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID(0, (ulong)(i + 1), 0, 0);
            var r = await btreeIndex.InsertAsync(key, (i + 1000) * 100, i + 1000);
            Assert.True(r.IsSuccess, $"Additional insert {i} failed: {r.Error}");
        }

        // Re-read the internal root and verify child hashes
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
            var actualHash = BTreeHasher.ComputeLeafContentHash(childLeaf);

            Assert.Equal(internalNode.ChildHashes[i], actualHash);
        }
    }

    [Fact]
    public async Task ThreeLevelTree_AllInternalNodes_ChildHashes_MatchChildren()
    {
        // Build a 3-level tree and verify that every internal node at every level
        // has ChildHashes matching the actual content hashes of its children.
        var filePath = Path.Combine(_tempDir, "child_hash_three_level.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build until we reach height 3
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

        // BFS traversal: verify child hashes at every internal node
        int nodesVerified = 0;
        var queue = new Queue<(long Offset, int Level)>();
        queue.Enqueue((btreeIndex.CurrentRoot.RootNodeBlockOffset, 1));

        while (queue.Count > 0)
        {
            var (offset, level) = queue.Dequeue();

            if (level >= btreeIndex.CurrentRoot.TreeHeight)
                continue; // Leaf level — nothing to verify

            var block = await ReadBlockAtOffset(rawBlockManager, offset);
            Assert.NotNull(block);
            var internalNode = BTreeNodeSerializer.DeserializeInternal(block!.Payload);

            for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
            {
                var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
                Assert.NotNull(childBlock);

                byte[] actualChildHash;
                if (level + 1 < btreeIndex.CurrentRoot.TreeHeight)
                {
                    // Child is an internal node
                    var childInternal = BTreeNodeSerializer.DeserializeInternal(childBlock!.Payload);
                    actualChildHash = BTreeHasher.ComputeInternalContentHash(childInternal);
                    queue.Enqueue((internalNode.ChildOffsets[i], level + 1));
                }
                else
                {
                    // Child is a leaf node
                    var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
                    actualChildHash = BTreeHasher.ComputeLeafContentHash(childLeaf);
                }

                Assert.Equal(internalNode.ChildHashes[i], actualChildHash);
            }

            nodesVerified++;
        }

        Assert.True(nodesVerified > 1, $"Should have verified multiple internal nodes, got {nodesVerified}");
    }

    [Fact]
    public async Task AfterDelete_InternalNode_ChildHashes_StillMatchChildren()
    {
        // After deleting a key, the affected leaf is rewritten and the parent's
        // ChildHashes are updated via copy-on-write. Verify they still match.
        var filePath = Path.Combine(_tempDir, "child_hash_after_delete.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build a 2-level tree
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Delete a key
        var deleteKey = new EmailHashedID(5, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(deleteKey);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Verify child hashes on the updated internal root
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
            var actualHash = BTreeHasher.ComputeLeafContentHash(childLeaf);

            Assert.Equal(internalNode.ChildHashes[i], actualHash);
        }
    }

    [Fact]
    public async Task AfterUpsert_InternalNode_ChildHashes_StillMatchChildren()
    {
        // After upserting an existing key in a multi-level tree, the leaf is rewritten
        // and the parent's ChildHashes must reflect the new leaf content hash.
        var filePath = Path.Combine(_tempDir, "child_hash_after_upsert.emdb");
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

        // Upsert: same key, different value
        var upsertKey = new EmailHashedID(3, 0, 0, 0);
        var upsertResult = await btreeIndex.InsertAsync(upsertKey, 99999, 99999);
        Assert.True(upsertResult.IsSuccess, $"Upsert failed: {upsertResult.Error}");

        // Verify child hashes
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
            var actualHash = BTreeHasher.ComputeLeafContentHash(childLeaf);

            Assert.Equal(internalNode.ChildHashes[i], actualHash);
        }
    }

    [Fact]
    public async Task StoredChildHash_EqualsChildNode_StoredNodeContentHash()
    {
        // The child node's own NodeContentHash field should also match the parent's
        // stored ChildHash. This verifies that the hash is consistent at both ends.
        var filePath = Path.Combine(_tempDir, "child_hash_equals_stored.emdb");
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

        for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);

            // Parent's stored hash must equal child's own NodeContentHash
            Assert.Equal(internalNode.ChildHashes[i], childLeaf.NodeContentHash);

            // And both must equal a freshly recomputed hash
            var recomputed = BTreeHasher.ComputeLeafContentHash(childLeaf);
            Assert.Equal(internalNode.ChildHashes[i], recomputed);
        }
    }

    [Fact]
    public async Task ChildHashes_AreAll32Bytes_NonZero()
    {
        // Every ChildHash stored in an internal node must be 32 bytes and non-zero
        // (a valid BLAKE3 hash of actual child content is overwhelmingly unlikely
        // to be all zeros).
        var filePath = Path.Combine(_tempDir, "child_hash_length.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        for (int i = 0; i < internalNode.ChildHashes.Length; i++)
        {
            Assert.Equal(32, internalNode.ChildHashes[i].Length);
            Assert.False(internalNode.ChildHashes[i].All(b => b == 0),
                $"ChildHash[{i}] should not be all zeros");
        }
    }

    [Fact]
    public async Task DistinctChildren_HaveDistinctChildHashes()
    {
        // Two different child leaves (with different entry sets) should produce
        // different content hashes stored in the parent internal node.
        var filePath = Path.Combine(_tempDir, "child_hash_distinct.emdb");
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

        Assert.True(internalNode.ChildHashes.Length >= 2);

        // All pairs of child hashes should be distinct
        for (int i = 0; i < internalNode.ChildHashes.Length; i++)
        {
            for (int j = i + 1; j < internalNode.ChildHashes.Length; j++)
            {
                Assert.False(internalNode.ChildHashes[i].SequenceEqual(internalNode.ChildHashes[j]),
                    $"ChildHash[{i}] and ChildHash[{j}] should differ for distinct children");
            }
        }
    }

    [Fact]
    public async Task InternalToInternal_ChildHashes_MatchChildInternalContentHashes()
    {
        // In a 3-level tree, the root internal node's children are also internal nodes.
        // Verify the root's ChildHashes match the actual content hashes of those
        // internal children (not just leaf children).
        var filePath = Path.Combine(_tempDir, "child_hash_internal_to_internal.emdb");
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

        // Read the root internal node
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootInternal = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Root's children are internal nodes (since height >= 3, level 2 nodes are internal)
        for (int i = 0; i < rootInternal.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, rootInternal.ChildOffsets[i]);
            Assert.NotNull(childBlock);

            var childInternal = BTreeNodeSerializer.DeserializeInternal(childBlock!.Payload);
            var actualHash = BTreeHasher.ComputeInternalContentHash(childInternal);

            Assert.Equal(rootInternal.ChildHashes[i], actualHash);
            Assert.Equal(childInternal.NodeContentHash, actualHash);
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
