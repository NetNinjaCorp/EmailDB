using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "Dead node space is reclaimed (file size
/// reduced)". After mutations (inserts + deletes), the append-only file grows
/// with dead B+-tree nodes that are no longer reachable. After compaction
/// (copying only the live subtree to a new file), the compacted file must be
/// strictly smaller than the original — proving that dead node space was
/// reclaimed.
/// </summary>
public class BTreeCompactionFileSizeTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeCompactionFileSizeTests()
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

    // --- Core: file size is reduced after compaction ---

    [Fact]
    public async Task Compaction_FileSizeIsReduced_AfterInserts()
    {
        var filePath = Path.Combine(_tempDir, "filesize_inserts.emdb");
        var compactedPath = Path.Combine(_tempDir, "filesize_inserts_compacted.emdb");

        long originalFileSize;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Insert enough entries to cause splits (which create dead nodes)
            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        Assert.True(compactedFileSize < originalFileSize,
            $"Compacted file ({compactedFileSize} bytes) should be smaller than " +
            $"original ({originalFileSize} bytes). Dead node space was not reclaimed.");
    }

    [Fact]
    public async Task Compaction_FileSizeIsReduced_AfterInsertsAndDeletes()
    {
        var filePath = Path.Combine(_tempDir, "filesize_deletes.emdb");
        var compactedPath = Path.Combine(_tempDir, "filesize_deletes_compacted.emdb");

        long originalFileSize;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Delete entries — each delete creates additional dead nodes
            for (int i = 1; i <= 10; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        Assert.True(compactedFileSize < originalFileSize,
            $"Compacted file ({compactedFileSize} bytes) should be smaller than " +
            $"original ({originalFileSize} bytes) after deletes created dead nodes.");
    }

    [Fact]
    public async Task Compaction_DeadBlockBytesAccountForSizeReduction()
    {
        // Verify that the size reduction corresponds to the dead blocks removed.
        var filePath = Path.Combine(_tempDir, "filesize_accounting.emdb");
        var compactedPath = Path.Combine(_tempDir, "filesize_accounting_compacted.emdb");

        long originalFileSize;
        long deadBlockBytes;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            // Compute dead block bytes
            var allBTreePositions = await CollectAllBTreeBlockPositions(rawBlockManager);
            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            var locations = rawBlockManager.GetBlockLocations();

            deadBlockBytes = 0;
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess &&
                    (readResult.Value.Type == BlockType.BTreeLeaf ||
                     readResult.Value.Type == BlockType.BTreeInternal ||
                     readResult.Value.Type == BlockType.IndexRoot))
                {
                    if (!livePositions.Contains(kvp.Value.Position))
                    {
                        deadBlockBytes += kvp.Value.Length;
                    }
                }
            }

            Assert.True(deadBlockBytes > 0,
                "Expected dead blocks before compaction for this test to be meaningful.");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;
        long sizeReduction = originalFileSize - compactedFileSize;

        // The size reduction should be at least the dead block bytes
        // (compacted file has no dead blocks, but may also omit old IndexRoots)
        Assert.True(sizeReduction >= deadBlockBytes,
            $"Size reduction ({sizeReduction} bytes) should be at least the dead " +
            $"block size ({deadBlockBytes} bytes). Original: {originalFileSize}, " +
            $"Compacted: {compactedFileSize}");
    }

    [Fact]
    public async Task Compaction_ThreeLevelTree_SignificantSizeReduction()
    {
        var filePath = Path.Combine(_tempDir, "filesize_3level.emdb");
        var compactedPath = Path.Combine(_tempDir, "filesize_3level_compacted.emdb");

        long originalFileSize;

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

            originalFileSize = rawBlockManager.FileLength;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        Assert.True(compactedFileSize < originalFileSize,
            $"3-level tree compacted file ({compactedFileSize} bytes) should be smaller " +
            $"than original ({originalFileSize} bytes).");

        // With a 3-level tree, many splits have occurred — expect meaningful reduction
        double reductionPercent = 100.0 * (originalFileSize - compactedFileSize) / originalFileSize;
        Assert.True(reductionPercent > 10,
            $"Expected >10% size reduction for 3-level tree, got {reductionPercent:F1}%.");
    }

    [Fact]
    public async Task Compaction_MixedMutations_FileSizeReduced()
    {
        var filePath = Path.Combine(_tempDir, "filesize_mixed.emdb");
        var compactedPath = Path.Combine(_tempDir, "filesize_mixed_compacted.emdb");

        long originalFileSize;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            // Phase 1: Insert initial batch
            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Phase 2: Delete some entries
            for (int i = 1; i <= 5; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            // Phase 3: Insert more entries
            for (int i = 200; i < 215; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        Assert.True(compactedFileSize < originalFileSize,
            $"Mixed-mutation compacted file ({compactedFileSize} bytes) should be " +
            $"smaller than original ({originalFileSize} bytes).");
    }

    [Fact]
    public async Task Compaction_CompactedFileHasZeroDeadBlocks()
    {
        // Verify that the compacted file contains no dead BTree blocks at all,
        // which directly explains the file size reduction.
        var filePath = Path.Combine(_tempDir, "zero_dead_blocks.emdb");
        var compactedPath = Path.Combine(_tempDir, "zero_dead_blocks_compacted.emdb");

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Confirm dead blocks exist in original
            var allOrig = await CollectAllBTreeBlockPositions(rawBlockManager);
            var liveOrig = await CollectLiveNodePositions(rawBlockManager);
            int deadBefore = allOrig.Count - liveOrig.Count;
            Assert.True(deadBefore > 0,
                $"Expected dead blocks before compaction, got {deadBefore}");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // In compacted file, every BTree block should be live
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var allCompacted = await CollectAllBTreeBlockPositions(verifyManager);
        var liveCompacted = await CollectLiveNodePositions(verifyManager);

        int deadAfter = allCompacted.Count - liveCompacted.Count;
        Assert.Equal(0, deadAfter);
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
            PreviousRootHash = new byte[32],
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
                PrevChainHash = new byte[32],
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

    private static async Task<HashSet<long>> CollectLiveNodePositions(RawBlockManager rawBlockManager)
    {
        var livePositions = new HashSet<long>();
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
            return livePositions;

        roots.Sort((a, b) => a.Position.CompareTo(b.Position));
        var latestRoot = roots[^1];

        livePositions.Add(latestRoot.Position);

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

    #endregion
}
