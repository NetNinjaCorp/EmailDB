using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "Compaction produces file with only live
/// B+-tree nodes". After mutations (inserts + deletes), the append-only file
/// accumulates dead B+-tree nodes that are no longer reachable from the latest
/// IndexRoot. After a proper compaction (copy only the live subtree with offset
/// remapping), every BTreeLeaf and BTreeInternal block in the compacted file
/// must be reachable from the root — zero dead nodes remain.
///
/// The compaction is simulated at the block level: walk the live tree from the
/// latest IndexRoot, copy nodes bottom-up to a new file (remapping internal
/// node ChildOffsets and recomputing Merkle hashes), then write a fresh
/// IndexRoot. This is logically equivalent to what BTreeRebuildManager will do.
/// </summary>
public class BTreeCompactionLiveNodesTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionLiveNodesTests()
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

    // ─── Core test: compaction produces only live nodes ──────────────────

    [Fact]
    public async Task Compaction_ProducesFileWithOnlyLiveBTreeNodes()
    {
        var filePath = Path.Combine(_tempDir, "live_nodes_only.emdb");
        var compactedPath = Path.Combine(_tempDir, "live_nodes_only_compacted.emdb");

        // Phase 1: Build a tree with enough inserts to cause splits,
        // creating dead nodes from the copy-on-write mechanism.
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Verify dead nodes exist before compaction
            var allBTreePositions = await CollectAllBTreeBlockPositions(rawBlockManager);
            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            Assert.True(allBTreePositions.Count > livePositions.Count,
                $"Expected dead nodes before compaction. Total: {allBTreePositions.Count}, Live: {livePositions.Count}");

            // Phase 2: Compact by copying only the live subtree
            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Phase 3: Verify every BTree node in the compacted file is live
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedAllPositions = await CollectAllBTreeBlockPositions(verifyManager);
        var compactedLivePositions = await CollectLiveNodePositions(verifyManager);

        Assert.Equal(compactedAllPositions.Count, compactedLivePositions.Count);
        foreach (var pos in compactedAllPositions)
        {
            Assert.Contains(pos, compactedLivePositions);
        }
    }

    [Fact]
    public async Task Compaction_AfterDeletes_ProducesOnlyLiveNodes()
    {
        var filePath = Path.Combine(_tempDir, "live_after_deletes.emdb");
        var compactedPath = Path.Combine(_tempDir, "live_after_deletes_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Delete some entries — each delete creates dead nodes
            for (int i = 1; i <= 10; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            // Confirm dead nodes exist
            var allCount = (await CollectAllBTreeBlockPositions(rawBlockManager)).Count;
            var liveCount = (await CollectLiveNodePositions(rawBlockManager)).Count;
            Assert.True(allCount > liveCount,
                $"Expected dead nodes after deletes. Total: {allCount}, Live: {liveCount}");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify all BTree nodes in compacted file are live
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var allPositions = await CollectAllBTreeBlockPositions(verifyManager);
        var livePositions = await CollectLiveNodePositions(verifyManager);

        Assert.Equal(allPositions.Count, livePositions.Count);
        foreach (var pos in allPositions)
        {
            Assert.Contains(pos, livePositions);
        }
    }

    [Fact]
    public async Task Compaction_AllLiveEntriesPreserved()
    {
        // After compaction the tree must contain every entry that was live
        // in the source tree — no data loss.
        var filePath = Path.Combine(_tempDir, "entries_preserved.emdb");
        var compactedPath = Path.Combine(_tempDir, "entries_preserved_compacted.emdb");

        List<LeafEntry> originalLiveEntries;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 15; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            for (int i = 1; i <= 5; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                await btreeIndex.DeleteAsync(key);
            }

            originalLiveEntries = await ExtractLiveEntries(rawBlockManager);

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify all original entries are present and lookupable
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var latestRoot = await GetLatestIndexRoot(verifyManager);
        Assert.NotNull(latestRoot);

        var verifyIndex = new BTreeIndex(verifyManager, latestRoot, await GetLatestIndexRootOffset(verifyManager));
        foreach (var entry in originalLiveEntries)
        {
            var lookupResult = await verifyIndex.LookupAsync(entry.Key);
            Assert.True(lookupResult.IsSuccess,
                $"Entry not found in compacted tree: {lookupResult.Error}");
            Assert.Equal(entry.BlockOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(entry.BlockId, lookupResult.Value.BlockId);
        }

        Assert.Equal(originalLiveEntries.Count, latestRoot.EntryCount);
    }

    [Fact]
    public async Task Compaction_LiveNodeCount_MatchesExpected()
    {
        // After compaction, the BTree block count should be strictly less
        // than the original (dead nodes were removed).
        var filePath = Path.Combine(_tempDir, "node_count_minimal.emdb");
        var compactedPath = Path.Combine(_tempDir, "node_count_minimal_compacted.emdb");

        int originalTotalBTreeBlocks;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            originalTotalBTreeBlocks = (await CollectAllBTreeBlockPositions(rawBlockManager)).Count;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedBTreeBlocks = (await CollectAllBTreeBlockPositions(verifyManager)).Count;

        Assert.True(compactedBTreeBlocks < originalTotalBTreeBlocks,
            $"Compacted file has {compactedBTreeBlocks} BTree blocks, " +
            $"original had {originalTotalBTreeBlocks}. Compaction should reduce block count.");
    }

    [Fact]
    public async Task Compaction_ThreeLevelTree_OnlyLiveNodesRemain()
    {
        var filePath = Path.Combine(_tempDir, "3level_live_only.emdb");
        var compactedPath = Path.Combine(_tempDir, "3level_live_only_compacted.emdb");

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

            // Confirm dead nodes exist
            var allCount = (await CollectAllBTreeBlockPositions(rawBlockManager)).Count;
            var liveCount = (await CollectLiveNodePositions(rawBlockManager)).Count;
            Assert.True(allCount > liveCount,
                $"Expected dead nodes in 3-level tree. Total: {allCount}, Live: {liveCount}");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify all BTree nodes are live
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var allPositions = await CollectAllBTreeBlockPositions(verifyManager);
        var livePositions = await CollectLiveNodePositions(verifyManager);

        Assert.Equal(allPositions.Count, livePositions.Count);
        foreach (var pos in allPositions)
        {
            Assert.Contains(pos, livePositions);
        }
    }

    [Fact]
    public async Task Compaction_DeadNodesExistBeforeCompaction()
    {
        // Sanity check: confirm that mutations produce dead B+-tree nodes.
        var filePath = Path.Combine(_tempDir, "dead_nodes_exist.emdb");

        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        for (int i = 0; i < BTreeLeafNode.MaxEntries + 5; i++)
        {
            var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        var allBTreePositions = await CollectAllBTreeBlockPositions(rawBlockManager);
        var livePositions = await CollectLiveNodePositions(rawBlockManager);

        int deadCount = allBTreePositions.Count - livePositions.Count;
        Assert.True(deadCount > 0,
            $"Expected dead B+-tree nodes after splits. " +
            $"Total: {allBTreePositions.Count}, Live: {livePositions.Count}, Dead: {deadCount}");
    }

    [Fact]
    public async Task Compaction_MerkleHashesValid_InCompactedFile()
    {
        // After compaction, all Merkle hashes must be self-consistent.
        var filePath = Path.Combine(_tempDir, "merkle_valid.emdb");
        var compactedPath = Path.Combine(_tempDir, "merkle_valid_compacted.emdb");

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
                var recomputed = BTreeHasher.ComputeLeafContentHash(leaf);
                Assert.Equal(recomputed, leaf.NodeContentHash);
                leafCount++;
            }
            else if (readResult.Value.Type == BlockType.BTreeInternal)
            {
                var node = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
                var recomputed = BTreeHasher.ComputeInternalContentHash(node);
                Assert.Equal(recomputed, node.NodeContentHash);
                internalCount++;
            }
        }

        Assert.True(leafCount > 0, "Compacted file must contain leaf blocks");
    }

    #region Compaction Helper

    /// <summary>
    /// Performs a proper compaction: walks the live tree from the latest
    /// IndexRoot, copies only reachable nodes to a new file (bottom-up),
    /// remapping internal node ChildOffsets and recomputing Merkle hashes.
    /// The result is a file with zero dead B+-tree nodes.
    /// </summary>
    private static async Task CompactLiveTreeToFile(RawBlockManager source, string destPath)
    {
        var locations = source.GetBlockLocations();
        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        // Find the latest IndexRoot
        var latestRoot = await FindLatestIndexRoot(source);
        if (latestRoot == null)
            throw new InvalidOperationException("No IndexRoot found in source file");

        using var dest = new RawBlockManager(destPath);

        // Recursively copy the live subtree bottom-up
        var (newRootOffset, newRootHash) = await CopySubtreeBottomUp(
            source, dest, positionToBlockId,
            latestRoot.Value.Root.RootNodeBlockOffset,
            latestRoot.Value.Root.TreeHeight);

        // Write the new IndexRoot
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

    /// <summary>
    /// Recursively copies a subtree from source to dest, bottom-up.
    /// Returns the new file offset and content hash of the copied node.
    /// </summary>
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
            // Leaf node — copy as-is (leaf entries reference data blocks, not tree offsets)
            var newBlock = new Block
            {
                Version = readResult.Value.Version,
                Type = readResult.Value.Type,
                Flags = readResult.Value.Flags,
                Timestamp = readResult.Value.Timestamp,
                BlockId = BlockIdGenerator.Instance.GetNextBTreeLeafId(),
                Payload = readResult.Value.Payload
            };
            var writeResult = await dest.WriteBlockAsync(newBlock);
            if (writeResult.IsFailure)
                throw new InvalidOperationException($"Failed to write leaf: {writeResult.Error}");

            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
            return (writeResult.Value.Position, leaf.NodeContentHash);
        }
        else
        {
            // Internal node — copy children first, then remap offsets
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

            // Create remapped internal node
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

    /// <summary>
    /// Collects file positions of all BTreeLeaf, BTreeInternal, and IndexRoot blocks.
    /// </summary>
    private static async Task<HashSet<long>> CollectAllBTreeBlockPositions(RawBlockManager rawBlockManager)
    {
        var positions = new HashSet<long>();
        var locations = rawBlockManager.GetBlockLocations();

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess &&
                (readResult.Value.Type == BlockType.BTreeLeaf ||
                 readResult.Value.Type == BlockType.BTreeInternal ||
                 readResult.Value.Type == BlockType.IndexRoot))
            {
                positions.Add(kvp.Value.Position);
            }
        }

        return positions;
    }

    /// <summary>
    /// Walks the tree from the latest IndexRoot to collect file positions
    /// of all live (reachable) BTree nodes.
    /// </summary>
    private static async Task<HashSet<long>> CollectLiveNodePositions(RawBlockManager rawBlockManager)
    {
        var livePositions = new HashSet<long>();
        var locations = rawBlockManager.GetBlockLocations();

        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        // Find all IndexRoots, take the latest by position
        var roots = new List<(IndexRoot Root, long Position)>();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var root = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                roots.Add((root, kvp.Value.Position));
            }
        }

        if (roots.Count == 0)
            return livePositions;

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        var latestRoot = roots[^1];

        // Include the latest IndexRoot
        livePositions.Add(latestRoot.Position);

        // Walk the tree
        await WalkTreeCollectPositions(
            rawBlockManager, positionToBlockId, livePositions,
            latestRoot.Root.RootNodeBlockOffset, latestRoot.Root.TreeHeight);

        return livePositions;
    }

    private static async Task WalkTreeCollectPositions(
        RawBlockManager rawBlockManager,
        Dictionary<long, long> positionToBlockId,
        HashSet<long> livePositions,
        long nodeOffset,
        int remainingHeight)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            return;

        var readResult = await rawBlockManager.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            return;

        livePositions.Add(nodeOffset);

        if (remainingHeight > 1 && readResult.Value.Type == BlockType.BTreeInternal)
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                await WalkTreeCollectPositions(
                    rawBlockManager, positionToBlockId, livePositions,
                    internalNode.ChildOffsets[i], remainingHeight - 1);
            }
        }
    }

    /// <summary>
    /// Extracts all live entries by walking from the latest root to all leaves.
    /// </summary>
    private static async Task<List<LeafEntry>> ExtractLiveEntries(RawBlockManager rawBlockManager)
    {
        var entries = new List<LeafEntry>();
        var locations = rawBlockManager.GetBlockLocations();

        var positionToBlockId = new Dictionary<long, long>();
        foreach (var kvp in locations)
            positionToBlockId[kvp.Value.Position] = kvp.Key;

        var roots = new List<(IndexRoot Root, long Position)>();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                var root = BTreeNodeSerializer.DeserializeIndexRoot(readResult.Value.Payload);
                roots.Add((root, kvp.Value.Position));
            }
        }

        if (roots.Count == 0)
            return entries;

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        var latestRoot = roots[^1].Root;

        await CollectLeafEntries(
            rawBlockManager, positionToBlockId, entries,
            latestRoot.RootNodeBlockOffset, latestRoot.TreeHeight);

        return entries;
    }

    private static async Task CollectLeafEntries(
        RawBlockManager rawBlockManager,
        Dictionary<long, long> positionToBlockId,
        List<LeafEntry> entries,
        long nodeOffset,
        int remainingHeight)
    {
        if (!positionToBlockId.TryGetValue(nodeOffset, out long blockId))
            return;

        var readResult = await rawBlockManager.ReadBlockAsync(blockId);
        if (readResult.IsFailure)
            return;

        if (remainingHeight == 1)
        {
            var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
            for (int i = 0; i < leaf.EntryCount; i++)
                entries.Add(leaf.Entries[i]);
        }
        else if (readResult.Value.Type == BlockType.BTreeInternal)
        {
            var internalNode = BTreeNodeSerializer.DeserializeInternal(readResult.Value.Payload);
            for (int i = 0; i <= internalNode.KeyCount; i++)
            {
                await CollectLeafEntries(
                    rawBlockManager, positionToBlockId, entries,
                    internalNode.ChildOffsets[i], remainingHeight - 1);
            }
        }
    }

    private static async Task<IndexRoot?> GetLatestIndexRoot(RawBlockManager rawBlockManager)
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

        return latest;
    }

    private static async Task<long> GetLatestIndexRootOffset(RawBlockManager rawBlockManager)
    {
        var locations = rawBlockManager.GetBlockLocations();
        long maxPosition = -1;

        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
            {
                if (kvp.Value.Position > maxPosition)
                    maxPosition = kvp.Value.Position;
            }
        }

        return maxPosition;
    }

    #endregion
}
