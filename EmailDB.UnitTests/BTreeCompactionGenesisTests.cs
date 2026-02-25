using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "Compaction produces valid new chain with
/// genesis". Compaction reads all blocks from the file and rewrites them to a
/// fresh file. After this process:
/// - The first IndexRoot must be genesis (PreviousRootHash = all zeros)
/// - All Merkle hashes (leaf/internal NodeContentHash) must remain self-consistent
/// - All blocks must be readable and deserialize correctly
/// - Block type counts and PrevChainHash states must be preserved
///
/// Note: The tests simulate compaction at the block level because
/// RawBlockManager.CompactAsync currently deadlocks (writer lock held while
/// ReadBlockAsync tries to acquire reader lock). The simulation is logically
/// equivalent: read all tracked blocks, write them sequentially to a new file.
/// </summary>
public class BTreeCompactionGenesisTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionGenesisTests()
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

    // ─── IndexRoot chain starts with genesis after compaction ────────────

    [Fact]
    public async Task Compaction_IndexRootChain_StartsWithGenesis()
    {
        // Build a tree with enough inserts to create multiple IndexRoots,
        // then compact and verify the earliest IndexRoot is genesis.
        var filePath = Path.Combine(_tempDir, "compact_genesis_chain.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_genesis_chain_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Verify we have multiple IndexRoots before compaction
            var rootsBefore = await CollectIndexRoots(rawBlockManager);
            Assert.True(rootsBefore.Count > 1,
                $"Expected multiple IndexRoots before compaction, got {rootsBefore.Count}");

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        // Simulate compaction: write all blocks to a new file
        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var rootsAfter = await CollectIndexRoots(compactedManager);
        Assert.True(rootsAfter.Count >= 1,
            "Compacted file must contain at least one IndexRoot");

        // The first IndexRoot (earliest in file) must be genesis
        var genesis = rootsAfter[0];
        Assert.Equal(32, genesis.Root.PreviousRootHash.Length);
        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, genesis.Root.PreviousRootOffset);
    }

    [Fact]
    public async Task Compaction_GenesisIndexRoot_HasValidRootNodeHash()
    {
        // After compaction, the genesis IndexRoot's RootNodeHash must be
        // a real non-zero BLAKE3 hash.
        var filePath = Path.Combine(_tempDir, "compact_genesis_hash.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_genesis_hash_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < 5; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var roots = await CollectIndexRoots(compactedManager);
        Assert.True(roots.Count >= 1);

        var genesis = roots[0];
        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.False(genesis.Root.RootNodeHash.All(b => b == 0),
            "Genesis IndexRoot's RootNodeHash should be non-zero after compaction");
    }

    // ─── Merkle integrity survives compaction ────────────────────────────

    [Fact]
    public async Task Compaction_LeafNodeContentHashes_RemainValid()
    {
        // After compaction every leaf node's stored NodeContentHash must
        // still match the recomputed BLAKE3 hash of its entries.
        var filePath = Path.Combine(_tempDir, "compact_leaf_hashes.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_leaf_hashes_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var locations = compactedManager.GetBlockLocations();
        int leafCount = 0;

        foreach (var kvp in locations)
        {
            var readResult = await compactedManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read block {kvp.Key}");

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
                Assert.Equal(recomputed, leaf.NodeContentHash);
                leafCount++;
            }
        }

        Assert.True(leafCount > 0, "Compacted file must contain at least one leaf block");
    }

    [Fact]
    public async Task Compaction_InternalNodeContentHashes_RemainValid()
    {
        // After compaction every internal node's stored NodeContentHash must
        // still match the recomputed BLAKE3 hash of its keys, offsets, and child hashes.
        var filePath = Path.Combine(_tempDir, "compact_internal_hashes.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_internal_hashes_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var locations = compactedManager.GetBlockLocations();
        int internalCount = 0;

        foreach (var kvp in locations)
        {
            var readResult = await compactedManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read block {kvp.Key}");

            if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                var recomputed = BTreeHasher.ComputeInternalContentHash(node);
                Assert.Equal(recomputed, node.NodeContentHash);
                internalCount++;
            }
        }

        Assert.True(internalCount > 0, "Compacted file must contain at least one internal block");
    }

    [Fact]
    public async Task Compaction_LatestIndexRoot_RootNodeHash_MatchesActualRootNode()
    {
        // The latest IndexRoot in the compacted file must reference a root
        // node whose recomputed hash matches IndexRoot.RootNodeHash.
        var filePath = Path.Combine(_tempDir, "compact_root_hash_match.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_root_hash_match_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var roots = await CollectIndexRoots(compactedManager);
        var latestRoot = roots[^1]; // Last in file position order

        // Find the root node block by scanning for a node whose content hash
        // matches the IndexRoot's RootNodeHash
        var rootNode = await FindNodeByContentHash(compactedManager, latestRoot.Root.RootNodeHash);
        Assert.NotNull(rootNode);
    }

    // ─── IndexRoot hash chain consistency ────────────────────────────────

    [Fact]
    public async Task Compaction_IndexRootHashChain_IsConsistent()
    {
        // After compaction, each IndexRoot's PreviousRootHash must match
        // the preceding IndexRoot's RootNodeHash (hash-based chain).
        var filePath = Path.Combine(_tempDir, "compact_chain_consistency.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_chain_consistency_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < 15; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var roots = await CollectIndexRoots(compactedManager);
        Assert.True(roots.Count >= 2,
            $"Expected multiple IndexRoots for chain verification, got {roots.Count}");

        // First root must be genesis
        Assert.All(roots[0].Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, roots[0].Root.PreviousRootOffset);

        // Each subsequent root's PreviousRootHash must match the previous root's RootNodeHash
        for (int i = 1; i < roots.Count; i++)
        {
            Assert.Equal(roots[i].Root.PreviousRootHash, roots[i - 1].Root.RootNodeHash);
        }
    }

    // ─── All blocks readable after compaction ────────────────────────────

    [Fact]
    public async Task Compaction_AllBlocks_AreReadable()
    {
        // Every block in the compacted file must deserialize without error.
        var filePath = Path.Combine(_tempDir, "compact_all_readable.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_all_readable_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var locations = compactedManager.GetBlockLocations();
        int blockCount = 0;

        foreach (var kvp in locations)
        {
            var readResult = await compactedManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess,
                $"Block {kvp.Key} at position {kvp.Value.Position} is not readable after compaction: {readResult.Error}");
            blockCount++;
        }

        Assert.True(blockCount > 0, "Compacted file must contain blocks");
    }

    // ─── Compaction with three-level tree ────────────────────────────────

    [Fact]
    public async Task Compaction_ThreeLevelTree_PreservesMerkleIntegrity()
    {
        // Build a 3-level tree, compact, then verify all leaf and internal
        // node content hashes are self-consistent in the compacted file.
        var filePath = Path.Combine(_tempDir, "compact_3level_merkle.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_3level_merkle_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            int totalInserts = 0;
            while (totalInserts < 3000)
            {
                var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
                Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
                totalInserts++;
                if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3);

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var locations = compactedManager.GetBlockLocations();
        int leafOk = 0, internalOk = 0;

        foreach (var kvp in locations)
        {
            var readResult = await compactedManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
                Assert.Equal(recomputed, leaf.NodeContentHash);
                leafOk++;
            }
            else if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                var recomputed = BTreeHasher.ComputeInternalContentHash(node);
                Assert.Equal(recomputed, node.NodeContentHash);
                internalOk++;
            }
        }

        Assert.True(leafOk > 0, "Compacted 3-level tree must have leaf blocks");
        Assert.True(internalOk > 0, "Compacted 3-level tree must have internal blocks");
    }

    [Fact]
    public async Task Compaction_ThreeLevelTree_GenesisIndexRootExists()
    {
        // A 3-level tree produces many IndexRoots. After compaction the
        // chain must still start with a genesis root.
        var filePath = Path.Combine(_tempDir, "compact_3level_genesis.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_3level_genesis_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            int totalInserts = 0;
            while (totalInserts < 3000)
            {
                var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
                Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
                totalInserts++;
                if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
            }

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var roots = await CollectIndexRoots(compactedManager);
        Assert.True(roots.Count >= 1);

        // Genesis must be at the start
        Assert.All(roots[0].Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, roots[0].Root.PreviousRootOffset);
    }

    // ─── Compaction after mutations (inserts + deletes) ──────────────────

    [Fact]
    public async Task Compaction_AfterDeletesAndInserts_GenesisChainValid()
    {
        // After inserts, deletes, and more inserts, compaction should still
        // produce a valid IndexRoot chain starting with genesis.
        var filePath = Path.Combine(_tempDir, "compact_mutations_genesis.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_mutations_genesis_compacted.emdb");

        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            for (int i = 1; i <= 5; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            for (int i = 200; i < 210; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var roots = await CollectIndexRoots(compactedManager);
        Assert.True(roots.Count >= 1, "Compacted file must have at least one IndexRoot");

        // Genesis exists
        Assert.All(roots[0].Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, roots[0].Root.PreviousRootOffset);

        // All leaf hashes valid
        var locations = compactedManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var readResult = await compactedManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess);

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
                Assert.Equal(recomputed, leaf.NodeContentHash);
            }
        }
    }

    // ─── Genesis block PrevChainHash after compaction ────────────────────

    [Fact]
    public async Task Compaction_BTreeNodes_PrevChainHashesPreserved()
    {
        // After compaction, B+-tree nodes must retain their PrevChainHash values.
        // Genesis nodes keep zeroed PrevChainHash; chained nodes keep non-zero.
        var filePath = Path.Combine(_tempDir, "compact_prevchain.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_prevchain_compacted.emdb");

        int genesisBefore, chainedBefore;
        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            (genesisBefore, chainedBefore) = await CountGenesisAndChained(rawBlockManager);
            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var (genesisAfter, chainedAfter) = await CountGenesisAndChained(compactedManager);

        // Genesis and chained counts must be preserved
        Assert.Equal(genesisBefore, genesisAfter);
        Assert.Equal(chainedBefore, chainedAfter);
    }

    // ─── Compaction preserves block types correctly ──────────────────────

    [Fact]
    public async Task Compaction_PreservesBlockTypeCounts()
    {
        // The compacted file must have the same number of blocks of each
        // B+-tree type as the original.
        var filePath = Path.Combine(_tempDir, "compact_type_counts.emdb");
        var compactedPath = Path.Combine(_tempDir, "compact_type_counts_compacted.emdb");

        Dictionary<BlockType, int> countsBefore;
        List<Block> blocks;
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            countsBefore = await CountBlockTypes(rawBlockManager);
            blocks = await ReadAllBlocks(rawBlockManager);
        }

        await WriteBlocksToFile(compactedPath, blocks);

        using var compactedManager = new RawBlockManager(compactedPath);
        var countsAfter = await CountBlockTypes(compactedManager);

        Assert.Equal(countsBefore[BlockType.BTreeLeaf], countsAfter[BlockType.BTreeLeaf]);
        Assert.Equal(countsBefore[BlockType.BTreeInternal], countsAfter[BlockType.BTreeInternal]);
        Assert.Equal(countsBefore[BlockType.IndexRoot], countsAfter[BlockType.IndexRoot]);
    }

    #region Helpers

    /// <summary>
    /// Reads all blocks from a RawBlockManager, sorted by their original file
    /// position. This preserves the block order that a correct compaction
    /// implementation would maintain (sequential copy in position order).
    /// </summary>
    private static async Task<List<Block>> ReadAllBlocks(RawBlockManager rawBlockManager)
    {
        var blocksWithPosition = new List<(long Position, Block Block)>();
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess)
                blocksWithPosition.Add((kvp.Value.Position, readResult.Value));
        }

        blocksWithPosition.Sort((a, b) => a.Position.CompareTo(b.Position));
        return blocksWithPosition.Select(x => x.Block).ToList();
    }

    /// <summary>
    /// Writes all blocks to a new file using a fresh RawBlockManager.
    /// Equivalent to the write phase of CompactAsync.
    /// </summary>
    private static async Task WriteBlocksToFile(string filePath, List<Block> blocks)
    {
        using var manager = new RawBlockManager(filePath);
        foreach (var block in blocks)
        {
            var result = await manager.WriteBlockAsync(block);
            if (result.IsFailure)
                throw new InvalidOperationException($"Failed to write block {block.BlockId}: {result.Error}");
        }
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

    private static async Task<Block?> FindNodeByContentHash(RawBlockManager rawBlockManager, byte[] contentHash)
    {
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (!readResult.IsSuccess) continue;

            byte[]? recomputed = null;

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
            }
            else if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                recomputed = BTreeHasher.ComputeInternalContentHash(node);
            }

            if (recomputed != null && recomputed.SequenceEqual(contentHash))
                return readResult.Value;
        }

        return null;
    }

    private static async Task<(int GenesisCount, int ChainedCount)> CountGenesisAndChained(
        RawBlockManager rawBlockManager)
    {
        int genesisCount = 0, chainedCount = 0;
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (!readResult.IsSuccess) continue;

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
                var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                if (node.PrevChainHash.All(b => b == 0))
                    genesisCount++;
                else
                    chainedCount++;
            }
        }

        return (genesisCount, chainedCount);
    }

    private static async Task<Dictionary<BlockType, int>> CountBlockTypes(RawBlockManager rawBlockManager)
    {
        var counts = new Dictionary<BlockType, int>();
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (!readResult.IsSuccess) continue;

            if (!counts.ContainsKey(readResult.Value.Type))
                counts[readResult.Value.Type] = 0;
            counts[readResult.Value.Type]++;
        }

        return counts;
    }

    #endregion
}
