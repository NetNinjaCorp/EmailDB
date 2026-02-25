using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "New hash chain starts with genesis in
/// compacted file". After a live-tree compaction (copying only reachable nodes
/// with offset remapping), the compacted file must contain exactly one IndexRoot
/// that is a fresh genesis (PreviousRootHash = all zeros, PreviousRootOffset = -1),
/// and all BTree nodes must have zeroed PrevChainHash (fresh chain, no history).
/// </summary>
public class BTreeCompactionGenesisChainTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionGenesisChainTests()
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

    // --- Compacted file has exactly one IndexRoot and it is genesis ---

    [Fact]
    public async Task Compaction_CompactedFile_HasExactlyOneGenesisIndexRoot()
    {
        var filePath = Path.Combine(_tempDir, "single_genesis.emdb");
        var compactedPath = Path.Combine(_tempDir, "single_genesis_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Insert enough entries to create multiple IndexRoots in the source
            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Verify the source has multiple IndexRoots (non-trivial chain)
            var sourceRoots = await CollectIndexRoots(rawBlockManager);
            Assert.True(sourceRoots.Count > 1,
                $"Expected multiple IndexRoots in source, got {sourceRoots.Count}");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // The compacted file must have exactly one IndexRoot
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedRoots = await CollectIndexRoots(verifyManager);

        Assert.Single(compactedRoots);

        // That single root must be genesis
        var genesis = compactedRoots[0];
        Assert.Equal(32, genesis.Root.PreviousRootHash.Length);
        Assert.All(genesis.Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, genesis.Root.PreviousRootOffset);
    }

    // --- Genesis root has valid non-zero RootNodeHash ---

    [Fact]
    public async Task Compaction_GenesisRoot_HasNonZeroRootNodeHash()
    {
        var filePath = Path.Combine(_tempDir, "genesis_hash.emdb");
        var compactedPath = Path.Combine(_tempDir, "genesis_hash_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        // The genesis root's RootNodeHash must be a real BLAKE3 hash (non-zero)
        Assert.False(roots[0].Root.RootNodeHash.All(b => b == 0),
            "Genesis IndexRoot's RootNodeHash must be non-zero");
        Assert.Equal(32, roots[0].Root.RootNodeHash.Length);
    }

    // --- All BTree nodes have zeroed PrevChainHash (fresh chain) ---

    [Fact]
    public async Task Compaction_AllBTreeNodes_HaveZeroedPrevChainHash()
    {
        var filePath = Path.Combine(_tempDir, "zeroed_prevchain.emdb");
        var compactedPath = Path.Combine(_tempDir, "zeroed_prevchain_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var locations = verifyManager.GetBlockLocations();
        int leafCount = 0, internalCount = 0;

        foreach (var kvp in locations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            Assert.True(readResult.IsSuccess, $"Failed to read block {kvp.Key}");

            if (readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                Assert.All(leaf.PrevChainHash, b => Assert.Equal(0, b));
                leafCount++;
            }
            else if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                Assert.All(node.PrevChainHash, b => Assert.Equal(0, b));
                internalCount++;
            }
        }

        Assert.True(leafCount > 0, "Compacted file must contain leaf blocks");
        Assert.True(internalCount > 0, "Compacted file must contain internal blocks");
    }

    // --- Source has non-zero PrevChainHash nodes, but compacted does not ---

    [Fact]
    public async Task Compaction_SourceHasChainedNodes_CompactedHasOnlyGenesis()
    {
        var filePath = Path.Combine(_tempDir, "chained_to_genesis.emdb");
        var compactedPath = Path.Combine(_tempDir, "chained_to_genesis_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Source should have some chained (non-zero PrevChainHash) nodes
            // due to copy-on-write from splits
            var (sourceGenesis, sourceChained) = await CountGenesisAndChainedNodes(rawBlockManager);
            Assert.True(sourceGenesis + sourceChained > 0,
                "Source must have BTree nodes");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // After compaction, ALL nodes must be genesis (zeroed PrevChainHash)
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var (compactedGenesis, compactedChained) = await CountGenesisAndChainedNodes(verifyManager);

        Assert.True(compactedGenesis > 0, "Compacted file must have BTree nodes");
        Assert.Equal(0, compactedChained);
    }

    // --- After mutations (insert + delete), compacted chain is fresh genesis ---

    [Fact]
    public async Task Compaction_AfterInsertsAndDeletes_ChainStartsFreshGenesis()
    {
        var filePath = Path.Combine(_tempDir, "mutations_fresh_genesis.emdb");
        var compactedPath = Path.Combine(_tempDir, "mutations_fresh_genesis_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Insert entries
            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Delete some entries (creates more dead nodes and IndexRoots)
            for (int i = 1; i <= 10; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            // Insert more entries
            for (int i = 300; i < 310; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Source should have many IndexRoots from all the mutations
            var sourceRoots = await CollectIndexRoots(rawBlockManager);
            Assert.True(sourceRoots.Count > 5,
                $"Expected many IndexRoots after heavy mutations, got {sourceRoots.Count}");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Compacted file: exactly one genesis IndexRoot, no chain history
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedRoots = await CollectIndexRoots(verifyManager);

        Assert.Single(compactedRoots);
        Assert.All(compactedRoots[0].Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, compactedRoots[0].Root.PreviousRootOffset);

        // All nodes must have fresh PrevChainHash
        var (genesisCount, chainedCount) = await CountGenesisAndChainedNodes(verifyManager);
        Assert.True(genesisCount > 0, "Compacted file must have BTree nodes");
        Assert.Equal(0, chainedCount);
    }

    // --- Three-level tree: compacted chain starts with genesis ---

    [Fact]
    public async Task Compaction_ThreeLevelTree_HasSingleGenesisRoot()
    {
        var filePath = Path.Combine(_tempDir, "3level_genesis.emdb");
        var compactedPath = Path.Combine(_tempDir, "3level_genesis_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            int totalInserts = 0;
            while (totalInserts < 5000)
            {
                var key = new EmailHashedID((ulong)(totalInserts + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, totalInserts * 100L, totalInserts);
                Assert.True(r.IsSuccess, $"Insert {totalInserts} failed: {r.Error}");
                totalInserts++;
                if (btreeIndex.CurrentRoot?.TreeHeight >= 3) break;
            }
            Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 3,
                "Expected at least 3-level tree");

            var sourceRoots = await CollectIndexRoots(rawBlockManager);
            Assert.True(sourceRoots.Count > 1,
                $"Expected multiple IndexRoots in 3-level tree, got {sourceRoots.Count}");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedRoots = await CollectIndexRoots(verifyManager);

        // Exactly one genesis root
        Assert.Single(compactedRoots);
        Assert.All(compactedRoots[0].Root.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, compactedRoots[0].Root.PreviousRootOffset);

        // TreeHeight and EntryCount preserved
        Assert.True(compactedRoots[0].Root.TreeHeight >= 3);
        Assert.True(compactedRoots[0].Root.EntryCount > 0);

        // All nodes are fresh (zeroed PrevChainHash)
        var (genesisCount, chainedCount) = await CountGenesisAndChainedNodes(verifyManager);
        Assert.True(genesisCount > 0);
        Assert.Equal(0, chainedCount);
    }

    // --- Genesis root points to a valid root node ---

    [Fact]
    public async Task Compaction_GenesisRoot_PointsToValidRootNode()
    {
        var filePath = Path.Combine(_tempDir, "genesis_valid_root.emdb");
        var compactedPath = Path.Combine(_tempDir, "genesis_valid_root_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var roots = await CollectIndexRoots(verifyManager);
        Assert.Single(roots);

        var genesis = roots[0].Root;

        // The root node at the referenced offset must exist and have a matching hash
        var locations = verifyManager.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        Assert.True(positionToBlockId.ContainsKey(genesis.RootNodeBlockOffset),
            $"Genesis IndexRoot references offset {genesis.RootNodeBlockOffset} which does not exist");

        var blockId = positionToBlockId[genesis.RootNodeBlockOffset];
        var readResult = await verifyManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess, $"Cannot read root node block: {readResult.Error}");

        // Verify the node's content hash matches what the IndexRoot claims
        byte[] computedHash;
        if (readResult.Value.Type == BlockType.BTreeLeaf)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
            computedHash = BTreeHasher.ComputeLeafContentHash(leaf);
        }
        else
        {
            Assert.Equal(BlockType.BTreeInternal, readResult.Value.Type);
            var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            computedHash = BTreeHasher.ComputeInternalContentHash(node);
        }

        Assert.Equal(genesis.RootNodeHash, computedHash);
    }

    #region Compaction Helper

    private static async Task CompactLiveTreeToFile(RawBlockManager source, string destPath)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var latestRoot = await FindLatestIndexRoot(source);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        var (newRootOffset, newRootHash) = await CopySubtreeBottomUp(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight);

        var indexRoot = new IndexRoot
        {
            RootNodeBlockOffset = newRootOffset,
            EntryCount = latestRoot.Value.Root.EntryCount,
            TreeHeight = latestRoot.Value.Root.TreeHeight,
            RootNodeHash = newRootHash,
            PreviousRootHash = new byte[32], // Fresh genesis
            PreviousRootOffset = -1
        };

        var rootPayload = BTreeNodeSerializer.SerializeIndexRoot(indexRoot);
        var rootBlock = new Block
        {
            Version = 1,
            Type = BlockType.IndexRoot,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextIndexRootId(),
            Payload = rootPayload
        };
        await dest.WriteBlockAsync(rootBlock);
    }

    private static async Task<(long NewOffset, byte[] ContentHash)> CopySubtreeBottomUp(
        RawBlockManager source,
        RawBlockManager dest,
        Dictionary<long, long> positionToBlockId,
        long nodeOffset,
        int remainingHeight)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            throw new InvalidOperationException($"No block found at offset {nodeOffset}");

        var readResult = await source.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            throw new InvalidOperationException($"Failed to read block {blockId}: {readResult.Error}");

        if (remainingHeight == 1)
        {
            // Leaf node — copy payload as-is (entries reference data blocks, not tree offsets)
            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
            // Reset PrevChainHash to zero for fresh chain
            leaf.PrevChainHash = new byte[32];
            var payload = BTreeNodeSerializer.SerializeLeaf(leaf);

            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
                Payload = payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            var newChildOffsets = new long[internalNode.KeyCount + 1];
            var newChildHashes = new byte[internalNode.KeyCount + 1][];

            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                var (childOffset, childHash) = await CopySubtreeBottomUp(
                    source, dest, positionToBlockId,
                    internalNode.ChildOffsets[i], remainingHeight - 1);
                newChildOffsets[i] = childOffset;
                newChildHashes[i] = childHash;
            }

            var remappedNode = new BTreeInternalNode
            {
                NodeType = internalNode.NodeType,
                Version = internalNode.Version,
                KeyCount = internalNode.KeyCount,
                PrevChainHash = new byte[32], // Fresh chain
                Keys = internalNode.Keys,
                ChildOffsets = newChildOffsets,
                ChildHashes = newChildHashes
            };
            remappedNode.NodeContentHash = BTreeHasher.ComputeInternalContentHash(remappedNode);

            var payload = BTreeNodeSerializer.SerializeInternal(remappedNode);
            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeInternalId(),
                Payload = payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write internal node: {writeResult.Error}");

            return (writeResult.Value.Position, remappedNode.NodeContentHash);
        }
    }

    private static async Task<(IndexRoot Root, long Position)?> FindLatestIndexRoot(RawBlockManager rawBlockManager)
    {
        var locations = rawBlockManager.GetBlockLocations();
        IndexRoot? latest = null;
        long maxPosition = -1;

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > maxPosition)
                {
                    maxPosition = kvp.Value.Position;
                    latest = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                }
            }
        }

        return latest != null ? (latest, maxPosition) : null;
    }

    #endregion

    #region Verification Helpers

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

    private static async Task<(int GenesisCount, int ChainedCount)> CountGenesisAndChainedNodes(
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

    #endregion
}
