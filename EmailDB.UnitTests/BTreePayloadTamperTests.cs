using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that modifying any block's payload causes chain verification failure.
/// Tests tamper detection by altering serialized payload bytes and checking that
/// recomputed BLAKE3 hashes no longer match the stored NodeContentHash.
/// </summary>
public class BTreePayloadTamperTests : IDisposable
{
    private readonly string _tempDir;

    public BTreePayloadTamperTests()
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
    public async Task TamperedLeafPayload_NodeContentHash_NoLongerMatches()
    {
        // Build a tree with a single leaf, then tamper with the serialized payload.
        // Recomputing the content hash must differ from the stored NodeContentHash.
        var filePath = Path.Combine(_tempDir, "tamper_leaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Read the leaf block from disk
        var leafBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(leafBlock);

        var originalLeaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);
        var originalHash = (byte[])originalLeaf.NodeContentHash.Clone();

        // Verify original hash is valid
        var recomputedOriginal = BTreeHasher.ComputeLeafContentHash(originalLeaf);
        Assert.Equal(originalHash, recomputedOriginal);

        // Tamper: flip a byte in the serialized payload (inside the entry data region)
        var tamperedPayload = (byte[])leafBlock.Payload.Clone();
        int entryDataStart = BTreeLeafNode.HeaderSize;
        tamperedPayload[entryDataStart + 5] ^= 0xFF; // Flip a byte in the first entry's key

        // Deserialize the tampered payload
        var tamperedLeaf = BTreeNodeSerializer.DeserializeLeaf(tamperedPayload);

        // The stored NodeContentHash came from the original data, so it must not match
        var recomputedTampered = BTreeHasher.ComputeLeafContentHash(tamperedLeaf);
        Assert.NotEqual(originalHash, recomputedTampered);

        // The tampered leaf's stored NodeContentHash is still the original (it was serialized
        // before tampering), so it mismatches the recomputed hash
        Assert.NotEqual(tamperedLeaf.NodeContentHash, recomputedTampered);
    }

    [Fact]
    public async Task TamperedInternalPayload_NodeContentHash_NoLongerMatches()
    {
        // Build a 2-level tree, then tamper with the internal node's serialized payload.
        var filePath = Path.Combine(_tempDir, "tamper_internal.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Build tree large enough to split (creates internal nodes)
        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Read the internal root block
        var internalBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(internalBlock);

        var originalNode = BTreeNodeSerializer.DeserializeInternal(internalBlock!.Payload);
        var originalHash = (byte[])originalNode.NodeContentHash.Clone();

        // Verify original hash is valid
        var recomputedOriginal = BTreeHasher.ComputeInternalContentHash(originalNode);
        Assert.Equal(originalHash, recomputedOriginal);

        // Tamper: flip a byte in the key data region of the serialized payload
        var tamperedPayload = (byte[])internalBlock.Payload.Clone();
        int keyDataStart = BTreeInternalNode.HeaderSize;
        tamperedPayload[keyDataStart + 3] ^= 0xFF;

        var tamperedNode = BTreeNodeSerializer.DeserializeInternal(tamperedPayload);
        var recomputedTampered = BTreeHasher.ComputeInternalContentHash(tamperedNode);

        Assert.NotEqual(originalHash, recomputedTampered);
        Assert.NotEqual(tamperedNode.NodeContentHash, recomputedTampered);
    }

    [Fact]
    public async Task TamperedLeafPayload_BreaksPrevChainHash_OnSuccessorNode()
    {
        // When a leaf's payload is tampered, recomputing its NodeContentHash produces a
        // different value. A successor node that chains to it via PrevChainHash will
        // have a mismatch: successor.PrevChainHash != recomputedHash(tampered predecessor).
        var filePath = Path.Combine(_tempDir, "tamper_chain.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert first key — genesis leaf
        var key1 = new EmailHashedID(1, 0, 0, 0);
        var r1 = await btreeIndex.InsertAsync(key1, 100, 1);
        Assert.True(r1.IsSuccess);

        // Read genesis leaf and capture its block data
        var genesisBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(genesisBlock);
        var genesisLeaf = BTreeNodeSerializer.DeserializeLeaf(genesisBlock!.Payload);
        var genesisOriginalHash = (byte[])genesisLeaf.NodeContentHash.Clone();

        // Insert second key — successor leaf chains to genesis via PrevChainHash
        var key2 = new EmailHashedID(2, 0, 0, 0);
        var r2 = await btreeIndex.InsertAsync(key2, 200, 2);
        Assert.True(r2.IsSuccess);

        var successorBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(successorBlock);
        var successorLeaf = BTreeNodeSerializer.DeserializeLeaf(successorBlock!.Payload);

        // Verify chain is valid: successor.PrevChainHash == genesis.NodeContentHash
        Assert.Equal(genesisOriginalHash, successorLeaf.PrevChainHash);

        // Now tamper with genesis leaf's payload
        var tamperedGenesisPayload = (byte[])genesisBlock.Payload.Clone();
        int entryDataStart = BTreeLeafNode.HeaderSize;
        tamperedGenesisPayload[entryDataStart + 10] ^= 0xFF;

        var tamperedGenesis = BTreeNodeSerializer.DeserializeLeaf(tamperedGenesisPayload);
        var recomputedGenesisHash = BTreeHasher.ComputeLeafContentHash(tamperedGenesis);

        // The successor's PrevChainHash no longer matches the tampered predecessor's recomputed hash
        Assert.NotEqual(successorLeaf.PrevChainHash, recomputedGenesisHash);
    }

    [Fact]
    public async Task TamperedChildOffset_InInternalNode_DetectedByContentHash()
    {
        // Tampering with child offsets in an internal node must cause its
        // recomputed content hash to differ from the stored NodeContentHash.
        var filePath = Path.Combine(_tempDir, "tamper_child_offset.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var internalBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(internalBlock);

        var originalNode = BTreeNodeSerializer.DeserializeInternal(internalBlock!.Payload);

        // Tamper: modify a child offset in the deserialized node
        var tamperedNode = BTreeNodeSerializer.DeserializeInternal((byte[])internalBlock.Payload.Clone());
        tamperedNode.ChildOffsets[0] += 999; // Point to wrong location

        var recomputedHash = BTreeHasher.ComputeInternalContentHash(tamperedNode);
        Assert.NotEqual(originalNode.NodeContentHash, recomputedHash);
    }

    [Fact]
    public async Task TamperedChildHash_InInternalNode_DetectedByContentHash()
    {
        // Tampering with a child's Merkle hash in an internal node must cause its
        // recomputed content hash to differ from the stored NodeContentHash.
        var filePath = Path.Combine(_tempDir, "tamper_child_hash.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        var internalBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(internalBlock);

        var originalNode = BTreeNodeSerializer.DeserializeInternal(internalBlock!.Payload);

        // Tamper: flip bytes in one child's Merkle hash
        var tamperedNode = BTreeNodeSerializer.DeserializeInternal((byte[])internalBlock.Payload.Clone());
        tamperedNode.ChildHashes[0][0] ^= 0xFF;
        tamperedNode.ChildHashes[0][15] ^= 0xFF;

        var recomputedHash = BTreeHasher.ComputeInternalContentHash(tamperedNode);
        Assert.NotEqual(originalNode.NodeContentHash, recomputedHash);
    }

    [Fact]
    public async Task TamperedLeafBlockOffset_DetectedByContentHash()
    {
        // Tampering with a leaf entry's BlockOffset must cause the leaf's
        // recomputed content hash to differ from the stored NodeContentHash.
        var filePath = Path.Combine(_tempDir, "tamper_leaf_blockoffset.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 2, 3, 4);
        var r = await btreeIndex.InsertAsync(key, 4096, 100);
        Assert.True(r.IsSuccess);

        var leafBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(leafBlock);

        var originalLeaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);
        var originalHash = (byte[])originalLeaf.NodeContentHash.Clone();

        // Tamper: change the entry's BlockOffset
        var tamperedLeaf = BTreeNodeSerializer.DeserializeLeaf((byte[])leafBlock.Payload.Clone());
        tamperedLeaf.Entries[0].BlockOffset = 99999;

        var recomputedHash = BTreeHasher.ComputeLeafContentHash(tamperedLeaf);
        Assert.NotEqual(originalHash, recomputedHash);
    }

    [Fact]
    public async Task TamperedLeafBlockId_DetectedByContentHash()
    {
        // Tampering with a leaf entry's BlockId must cause the leaf's
        // recomputed content hash to differ from the stored NodeContentHash.
        var filePath = Path.Combine(_tempDir, "tamper_leaf_blockid.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 2, 3, 4);
        var r = await btreeIndex.InsertAsync(key, 4096, 100);
        Assert.True(r.IsSuccess);

        var leafBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(leafBlock);

        var originalLeaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);
        var originalHash = (byte[])originalLeaf.NodeContentHash.Clone();

        // Tamper: change the entry's BlockId
        var tamperedLeaf = BTreeNodeSerializer.DeserializeLeaf((byte[])leafBlock.Payload.Clone());
        tamperedLeaf.Entries[0].BlockId = 77777;

        var recomputedHash = BTreeHasher.ComputeLeafContentHash(tamperedLeaf);
        Assert.NotEqual(originalHash, recomputedHash);
    }

    [Fact]
    public async Task TamperedLeafKey_DetectedByContentHash()
    {
        // Tampering with a leaf entry's key must cause the leaf's
        // recomputed content hash to differ from the stored NodeContentHash.
        var filePath = Path.Combine(_tempDir, "tamper_leaf_key.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 2, 3, 4);
        var r = await btreeIndex.InsertAsync(key, 4096, 100);
        Assert.True(r.IsSuccess);

        var leafBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(leafBlock);

        var originalLeaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);
        var originalHash = (byte[])originalLeaf.NodeContentHash.Clone();

        // Tamper: change the entry's key
        var tamperedLeaf = BTreeNodeSerializer.DeserializeLeaf((byte[])leafBlock.Payload.Clone());
        tamperedLeaf.Entries[0].Key = new EmailHashedID(999, 888, 777, 666);

        var recomputedHash = BTreeHasher.ComputeLeafContentHash(tamperedLeaf);
        Assert.NotEqual(originalHash, recomputedHash);
    }

    [Fact]
    public async Task FullTree_TamperAnyLeaf_IndexRootRootNodeHash_Mismatches()
    {
        // Build a multi-level tree, tamper with a leaf payload, then verify that
        // the IndexRoot.RootNodeHash chain is broken because the Merkle tree
        // integrity from root to tampered leaf is invalid.
        var filePath = Path.Combine(_tempDir, "tamper_full_tree.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Read root internal node
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Read a child leaf
        var childLeafBlock = await ReadBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[0]);
        Assert.NotNull(childLeafBlock);
        var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childLeafBlock!.Payload);

        // Verify: the stored child hash in the internal node matches the leaf's actual hash
        var actualChildHash = BTreeHasher.ComputeLeafContentHash(childLeaf);
        Assert.Equal(rootNode.ChildHashes[0], actualChildHash);

        // Tamper: modify the leaf's first entry
        childLeaf.Entries[0].BlockOffset = 123456789;
        var tamperedChildHash = BTreeHasher.ComputeLeafContentHash(childLeaf);

        // The internal node's stored child hash no longer matches
        Assert.NotEqual(rootNode.ChildHashes[0], tamperedChildHash);

        // Therefore, recomputing the internal node's hash with the tampered child hash
        // would produce a different root hash, breaking IndexRoot.RootNodeHash
        var clonedNode = BTreeNodeSerializer.DeserializeInternal((byte[])rootBlock.Payload.Clone());
        clonedNode.ChildHashes[0] = tamperedChildHash;
        var recomputedRootHash = BTreeHasher.ComputeInternalContentHash(clonedNode);
        Assert.NotEqual(btreeIndex.CurrentRoot.RootNodeHash, recomputedRootHash);
    }

    [Fact]
    public async Task SingleBitFlip_InLeafPayload_Detected()
    {
        // Even a single-bit change in the payload must be detected by the hash.
        var filePath = Path.Combine(_tempDir, "tamper_single_bit.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(42, 84, 126, 168);
        var r = await btreeIndex.InsertAsync(key, 1000, 1);
        Assert.True(r.IsSuccess);

        var leafBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(leafBlock);

        var originalLeaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);
        var originalHash = (byte[])originalLeaf.NodeContentHash.Clone();

        // Flip a single bit in the entry data portion of the serialized payload
        var tamperedPayload = (byte[])leafBlock.Payload.Clone();
        int entryDataStart = BTreeLeafNode.HeaderSize;
        tamperedPayload[entryDataStart] ^= 0x01; // Flip just the least significant bit

        var tamperedLeaf = BTreeNodeSerializer.DeserializeLeaf(tamperedPayload);
        var recomputedHash = BTreeHasher.ComputeLeafContentHash(tamperedLeaf);

        Assert.NotEqual(originalHash, recomputedHash);
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
