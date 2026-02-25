using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "Chain handles genesis block (first block
/// has zeroed PrevChainHash)". Genesis blocks are the first version of a node
/// with no predecessor — their PrevChainHash is 32 bytes of zeros. This file
/// ensures genesis blocks are correctly created across all node types (leaf,
/// internal, IndexRoot), survive serialization, and pass all verification modes.
/// </summary>
public class BTreeGenesisBlockTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeGenesisBlockTests()
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

    // ─── Leaf genesis ───────────────────────────────────────────────────

    [Fact]
    public async Task GenesisLeaf_PrevChainHash_IsAllZeros()
    {
        // The very first leaf in an empty tree is a genesis block.
        var filePath = Path.Combine(_tempDir, "genesis_leaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(1, 2, 3, 4);
        var result = await btreeIndex.InsertAsync(key, 4096, 100);
        Assert.True(result.IsSuccess, $"Insert failed: {result.Error}");

        var leaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(leaf);

        Assert.Equal(32, leaf!.PrevChainHash.Length);
        Assert.All(leaf.PrevChainHash, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task GenesisLeaf_NodeContentHash_IsNonZero()
    {
        // A genesis block has zeroed PrevChainHash but a valid non-zero
        // BLAKE3 NodeContentHash computed from its entries.
        var filePath = Path.Combine(_tempDir, "genesis_leaf_hash.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(10, 20, 30, 40);
        var result = await btreeIndex.InsertAsync(key, 1000, 1);
        Assert.True(result.IsSuccess);

        var leaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(leaf);

        // PrevChainHash is zeroed (genesis)
        Assert.All(leaf!.PrevChainHash, b => Assert.Equal(0, b));
        // NodeContentHash is a real BLAKE3 hash, not zeros
        Assert.False(leaf.NodeContentHash.All(b => b == 0),
            "Genesis leaf's NodeContentHash should be non-zero");
        // Verify the stored hash matches a recomputed hash
        var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
        Assert.Equal(recomputed, leaf.NodeContentHash);
    }

    [Fact]
    public async Task GenesisLeaf_Serialization_PreservesZeroPrevChainHash()
    {
        // PrevChainHash = all zeros must survive serialize → deserialize round-trip.
        var filePath = Path.Combine(_tempDir, "genesis_leaf_roundtrip.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var result = await btreeIndex.InsertAsync(new EmailHashedID(5, 5, 5, 5), 100, 1);
        Assert.True(result.IsSuccess);

        var leaf = await ReadLeafAtRootOffset(rawBlockManager, btreeIndex);
        Assert.NotNull(leaf);

        var serialized = BTreeNodeSerializer.SerializeLeaf(leaf!);
        var deserialized = BTreeNodeSerializer.DeserializeLeaf(serialized);

        Assert.Equal(32, deserialized.PrevChainHash.Length);
        Assert.All(deserialized.PrevChainHash, b => Assert.Equal(0, b));
        Assert.Equal(leaf.NodeContentHash, deserialized.NodeContentHash);
    }

    // ─── Internal node genesis ──────────────────────────────────────────

    [Fact]
    public async Task GenesisInternalNode_PrevChainHash_IsAllZeros()
    {
        // When the root leaf splits, a new internal root is created as a genesis block.
        var filePath = Path.Combine(_tempDir, "genesis_internal.emdb");
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

        Assert.Equal(32, internalNode.PrevChainHash.Length);
        Assert.All(internalNode.PrevChainHash, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task GenesisInternalNode_NodeContentHash_IsNonZero()
    {
        // The genesis internal node should have a valid BLAKE3 hash despite zeroed PrevChainHash.
        var filePath = Path.Combine(_tempDir, "genesis_internal_hash.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 1; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        Assert.All(internalNode.PrevChainHash, b => Assert.Equal(0, b));
        Assert.False(internalNode.NodeContentHash.All(b => b == 0),
            "Genesis internal node's NodeContentHash should be non-zero");
        var recomputed = BTreeHasher.ComputeInternalContentHash(internalNode);
        Assert.Equal(recomputed, internalNode.NodeContentHash);
    }

    [Fact]
    public async Task RootSplit_NewRoot_IsGenesisInternal()
    {
        // When the root internal node splits (height 2 → 3), the new root
        // should be a genesis block with zeroed PrevChainHash.
        var filePath = Path.Combine(_tempDir, "root_split_genesis.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = 0;
        while (totalInserts < 10000)
        {
            var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
            Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
            totalInserts++;
            if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3,
            "Should have reached height 3 via root split");

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // The brand-new root (born from a root split) is a genesis block
        Assert.All(rootNode.PrevChainHash, b => Assert.Equal(0, b));
    }

    // ─── IndexRoot genesis ──────────────────────────────────────────────

    [Fact]
    public async Task GenesisIndexRoot_PreviousRootHash_IsAllZeros()
    {
        // The very first IndexRoot written has no predecessor.
        var filePath = Path.Combine(_tempDir, "genesis_indexroot.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var result = await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        Assert.True(result.IsSuccess);

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 1);

        var genesis = indexRoots[0];
        Assert.Equal(32, genesis.Root.PreviousRootHash.Length);
        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, genesis.Root.PreviousRootOffset);
    }

    [Fact]
    public async Task GenesisIndexRoot_RootNodeHash_IsNonZero()
    {
        // The genesis IndexRoot should have a valid RootNodeHash pointing
        // to the actual root node's content hash.
        var filePath = Path.Combine(_tempDir, "genesis_indexroot_hash.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var result = await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        Assert.True(result.IsSuccess);

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        var genesis = indexRoots[0];

        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.False(genesis.Root.RootNodeHash.All(b => b == 0),
            "Genesis IndexRoot's RootNodeHash should be non-zero");
    }

    // ─── Split genesis blocks ───────────────────────────────────────────

    [Fact]
    public async Task LeafSplit_RightChild_IsGenesisBlock()
    {
        // On a leaf split, the right child is a brand-new node with no predecessor.
        var filePath = Path.Combine(_tempDir, "split_right_genesis.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Trigger split
        var splitResult = await btreeIndex.InsertAsync(new EmailHashedID(0, 0, 0, 1), 9999, 9999);
        Assert.True(splitResult.IsSuccess);
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        var rightBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[1]);
        Assert.NotNull(rightBlock);
        var rightLeaf = BTreeNodeSerializer.DeserializeLeaf(rightBlock!.Payload);

        // Right child is a genesis block
        Assert.All(rightLeaf.PrevChainHash, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task LeafSplit_LeftChild_IsNotGenesis()
    {
        // On a leaf split, the left child chains to the original leaf (not genesis).
        var filePath = Path.Combine(_tempDir, "split_left_not_genesis.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }

        var splitResult = await btreeIndex.InsertAsync(new EmailHashedID(0, 0, 0, 1), 9999, 9999);
        Assert.True(splitResult.IsSuccess);
        Assert.Equal((ushort)2, btreeIndex.CurrentRoot!.TreeHeight);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        var leftBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[0]);
        Assert.NotNull(leftBlock);
        var leftLeaf = BTreeNodeSerializer.DeserializeLeaf(leftBlock!.Payload);

        // Left child chains to the original leaf — NOT genesis
        Assert.False(leftLeaf.PrevChainHash.All(b => b == 0),
            "Left child on split should chain to original, not be genesis");
    }

    [Fact]
    public async Task FileContainsBothGenesisAndChainedBlocks()
    {
        // The file should contain both genesis blocks (first versions of nodes)
        // and chained blocks (updated versions whose PrevChainHash is non-zero).
        // Genesis blocks exist as historical versions; after copy-on-write updates,
        // the active tree nodes become chained.
        var filePath = Path.Combine(_tempDir, "mixed_genesis_chained.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        int genesisCount = 0;
        int chainedCount = 0;

        // Scan ALL blocks in the file (including historical versions)
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                if (leaf.PrevChainHash.All(b => b == 0))
                    genesisCount++;
                else
                    chainedCount++;
            }
            else if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                if (internalNode.PrevChainHash.All(b => b == 0))
                    genesisCount++;
                else
                    chainedCount++;
            }
        }

        Assert.True(genesisCount > 0,
            "File should contain at least one genesis block");
        Assert.True(chainedCount > 0,
            "File should contain at least one chained (non-genesis) block");
    }

    // ─── Verification passes with genesis blocks ────────────────────────

    [Fact]
    public async Task GenesisOnlyTree_PassesFullVerification()
    {
        // A single-leaf tree (only genesis blocks) passes full verification.
        var filePath = Path.Combine(_tempDir, "genesis_only_verify.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var result = await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        Assert.True(result.IsSuccess);
        Assert.Equal((ushort)1, btreeIndex.CurrentRoot!.TreeHeight);

        // Full verification: IndexRoot.RootNodeHash must match leaf's content hash
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var leaf = BTreeNodeSerializer.DeserializeLeaf(rootBlock!.Payload);
        var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);

        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, recomputed);
    }

    [Fact]
    public async Task GenesisIndexRoot_QuickVerification_Passes()
    {
        // Quick verification of a single genesis IndexRoot should pass.
        var filePath = Path.Combine(_tempDir, "genesis_quick_verify.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var result = await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        Assert.True(result.IsSuccess);

        var indexRoots = await CollectIndexRoots(rawBlockManager);
        Assert.True(indexRoots.Count >= 1);

        // Walk the chain: genesis root has PreviousRootHash = zeros, PreviousRootOffset = -1
        var genesis = indexRoots[0];
        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, genesis.Root.PreviousRootOffset);

        // RootNodeHash should match the actual leaf node
        var leafBlock = await ReadBlockAtOffset(rawBlockManager, genesis.Root.RootNodeBlockOffset);
        Assert.NotNull(leafBlock);
        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock!.Payload);
        var leafHash = BTreeHasher.ComputeLeafContentHash(leaf);
        Assert.Equal(genesis.Root.RootNodeHash, leafHash);
    }

    [Fact]
    public async Task TreeWithGenesisBlocks_AfterSplit_PassesFullVerification()
    {
        // After a split (which creates genesis blocks), the entire tree
        // passes full Merkle verification.
        var filePath = Path.Combine(_tempDir, "genesis_after_split_verify.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Full Merkle verification: root → all children
        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var rootNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);
        var rootHash = BTreeHasher.ComputeInternalContentHash(rootNode);

        // IndexRoot.RootNodeHash matches actual root
        Assert.Equal(btreeIndex.CurrentRoot.RootNodeHash, rootHash);

        // All child hashes match actual children (regardless of genesis status)
        for (int i = 0; i < rootNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, rootNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);
            var childLeaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
            var childHash = BTreeHasher.ComputeLeafContentHash(childLeaf);
            Assert.Equal(rootNode.ChildHashes[i], childHash);
        }
    }

    // ─── Genesis block uniqueness ───────────────────────────────────────

    [Fact]
    public async Task TwoGenesisLeaves_HaveDifferentContentHashes()
    {
        // Two genesis blocks with different entries should have different
        // NodeContentHashes despite both having zeroed PrevChainHash.
        var filePath = Path.Combine(_tempDir, "genesis_unique_hashes.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Fill to capacity and split — creates two leaves, right is genesis
        for (int i = 0; i < BTreeLeafNode.MaxEntries; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var splitResult = await btreeIndex.InsertAsync(new EmailHashedID(0, 0, 0, 1), 9999, 9999);
        Assert.True(splitResult.IsSuccess);

        var rootBlock = await ReadBlockAtOffset(rawBlockManager, btreeIndex.CurrentRoot!.RootNodeBlockOffset);
        Assert.NotNull(rootBlock);
        var internalNode = BTreeNodeSerializer.DeserializeInternal(rootBlock!.Payload);

        // Gather all child leaf hashes
        var hashes = new List<byte[]>();
        for (int i = 0; i < internalNode.ChildOffsets.Length; i++)
        {
            var childBlock = await ReadBlockAtOffset(rawBlockManager, internalNode.ChildOffsets[i]);
            Assert.NotNull(childBlock);
            var leaf = BTreeNodeSerializer.DeserializeLeaf(childBlock!.Payload);
            hashes.Add(leaf.NodeContentHash);
        }

        // All content hashes should be unique even if PrevChainHash is the same
        for (int i = 0; i < hashes.Count; i++)
            for (int j = i + 1; j < hashes.Count; j++)
                Assert.NotEqual(hashes[i], hashes[j]);
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

    private class IndexRootEntry
    {
        public IndexRoot Root { get; }
        public long BlockOffset { get; }

        public IndexRootEntry(IndexRoot root, long blockOffset)
        {
            Root = root;
            BlockOffset = blockOffset;
        }
    }

    private static async Task<List<IndexRootEntry>> CollectIndexRoots(RawBlockManager rawBlockManager)
    {
        var roots = new List<(IndexRoot Root, long Position)>();
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var root = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                roots.Add((root, kvp.Value.Position));
            }
        }

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        return roots.Select(r => new IndexRootEntry(r.Root, r.Position)).ToList();
    }

    #endregion
}
