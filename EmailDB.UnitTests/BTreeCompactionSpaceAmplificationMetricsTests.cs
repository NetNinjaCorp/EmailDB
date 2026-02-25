using System.Diagnostics;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the acceptance criterion: "Space amplification metrics documented
/// with and without compaction". After mutations (inserts + deletes), the
/// append-only file accumulates dead B+-tree nodes. We measure and document
/// space amplification metrics BEFORE compaction (showing the cost of dead
/// blocks) and AFTER compaction (showing reclaimed space), producing a full
/// comparison report.
/// </summary>
public class BTreeCompactionSpaceAmplificationMetricsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;

    public BTreeCompactionSpaceAmplificationMetricsTests(ITestOutputHelper output)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _output = output;
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // --- Core: space amplification ratio is computed correctly ---

    [Fact]
    public async Task Compaction_SpaceAmplificationRatio_IsGreaterThanOne()
    {
        var filePath = Path.Combine(_tempDir, "amp_ratio.emdb");
        var compactedPath = Path.Combine(_tempDir, "amp_ratio_compacted.emdb");

        long originalFileSize;

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

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        // Space amplification ratio = original / compacted
        // Must be > 1.0 when dead blocks existed
        double spaceAmplificationRatio = (double)originalFileSize / compactedFileSize;
        long spaceReclaimedBytes = originalFileSize - compactedFileSize;
        double reductionPercent = 100.0 * spaceReclaimedBytes / originalFileSize;

        _output.WriteLine("");
        _output.WriteLine("=== Space Amplification Ratio (insert-only workload) ===");
        _output.WriteLine($"  Without compaction (original) : {originalFileSize:N0} bytes");
        _output.WriteLine($"  With compaction (compacted)   : {compactedFileSize:N0} bytes");
        _output.WriteLine($"  Space amplification ratio     : {spaceAmplificationRatio:F3}x");
        _output.WriteLine($"  Space reclaimed               : {spaceReclaimedBytes:N0} bytes ({reductionPercent:F1}%)");
        _output.WriteLine("");

        Assert.True(spaceAmplificationRatio > 1.0,
            $"Space amplification ratio should be > 1.0 (original has dead blocks). " +
            $"Got {spaceAmplificationRatio:F3} (original: {originalFileSize}, compacted: {compactedFileSize})");
    }

    [Fact]
    public async Task Compaction_MetricsReport_ContainsAllRequiredFields()
    {
        var filePath = Path.Combine(_tempDir, "metrics_fields.emdb");
        var compactedPath = Path.Combine(_tempDir, "metrics_fields_compacted.emdb");

        long originalFileSize;
        int deadBlocksBefore;
        long deadBlockBytesBefore;
        int totalBTreeBlocksBefore;
        int liveBTreeBlocksBefore;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Delete some entries to create more dead nodes
            for (int i = 1; i <= 5; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            // Compute pre-compaction metrics
            var allPositions = await CollectAllBTreeBlockPositions(rawBlockManager);
            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            totalBTreeBlocksBefore = allPositions.Count;
            liveBTreeBlocksBefore = livePositions.Count;
            deadBlocksBefore = totalBTreeBlocksBefore - liveBTreeBlocksBefore;

            var locations = rawBlockManager.GetBlockLocations();
            deadBlockBytesBefore = 0;
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess &&
                    (readResult.Value.Type == BlockType.BTreeLeaf ||
                     readResult.Value.Type == BlockType.BTreeInternal ||
                     readResult.Value.Type == BlockType.IndexRoot))
                {
                    if (!livePositions.Contains(kvp.Value.Position))
                        deadBlockBytesBefore += kvp.Value.Length;
                }
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        // Compute all space amplification metrics
        long spaceReclaimedBytes = originalFileSize - compactedFileSize;
        double reductionPercent = 100.0 * spaceReclaimedBytes / originalFileSize;
        double spaceAmplificationRatio = (double)originalFileSize / compactedFileSize;

        _output.WriteLine("");
        _output.WriteLine("=== Full Metrics Report (insert + delete workload) ===");
        _output.WriteLine("");
        _output.WriteLine("  WITHOUT Compaction:");
        _output.WriteLine($"    File size            : {originalFileSize:N0} bytes");
        _output.WriteLine($"    Total BTree blocks   : {totalBTreeBlocksBefore}");
        _output.WriteLine($"    Live BTree blocks    : {liveBTreeBlocksBefore}");
        _output.WriteLine($"    Dead BTree blocks    : {deadBlocksBefore}");
        _output.WriteLine($"    Dead block bytes     : {deadBlockBytesBefore:N0} bytes");
        _output.WriteLine($"    Dead block ratio     : {100.0 * deadBlocksBefore / totalBTreeBlocksBefore:F1}% of blocks");
        _output.WriteLine("");
        _output.WriteLine("  WITH Compaction:");
        _output.WriteLine($"    File size            : {compactedFileSize:N0} bytes");
        _output.WriteLine($"    Space reclaimed      : {spaceReclaimedBytes:N0} bytes ({reductionPercent:F1}%)");
        _output.WriteLine($"    Amplification ratio  : {spaceAmplificationRatio:F3}x");
        _output.WriteLine("");

        // Verify all required metric fields are computable and meaningful
        Assert.True(originalFileSize > 0, "Original file size must be reported");
        Assert.True(compactedFileSize > 0, "Compacted file size must be reported");
        Assert.True(spaceReclaimedBytes > 0, "Space reclaimed bytes must be positive");
        Assert.True(reductionPercent > 0 && reductionPercent < 100,
            $"Reduction percent must be between 0 and 100, got {reductionPercent:F1}%");
        Assert.True(spaceAmplificationRatio > 1.0,
            $"Space amplification ratio must be > 1.0, got {spaceAmplificationRatio:F3}");
        Assert.True(deadBlocksBefore > 0, "Dead block count before compaction must be reported");
        Assert.True(deadBlockBytesBefore > 0, "Dead block bytes before compaction must be reported");
        Assert.True(totalBTreeBlocksBefore > liveBTreeBlocksBefore,
            "Total and live block counts must be independently reported");
    }

    [Fact]
    public async Task Compaction_PerBlockTypeMetrics_AreReported()
    {
        var filePath = Path.Combine(_tempDir, "block_type_metrics.emdb");
        var compactedPath = Path.Combine(_tempDir, "block_type_metrics_compacted.emdb");

        int originalLeafCount = 0, originalInternalCount = 0, originalIndexRootCount = 0;
        long originalLeafBytes = 0, originalInternalBytes = 0, originalIndexRootBytes = 0;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            // Collect per-block-type metrics from original file
            var locations = rawBlockManager.GetBlockLocations();
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (!readResult.IsSuccess) continue;

                switch (readResult.Value.Type)
                {
                    case BlockType.BTreeLeaf:
                        originalLeafCount++;
                        originalLeafBytes += kvp.Value.Length;
                        break;
                    case BlockType.BTreeInternal:
                        originalInternalCount++;
                        originalInternalBytes += kvp.Value.Length;
                        break;
                    case BlockType.IndexRoot:
                        originalIndexRootCount++;
                        originalIndexRootBytes += kvp.Value.Length;
                        break;
                }
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Collect per-block-type metrics from compacted file
        int compactedLeafCount = 0, compactedInternalCount = 0, compactedIndexRootCount = 0;
        long compactedLeafBytes = 0, compactedInternalBytes = 0, compactedIndexRootBytes = 0;

        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedLocations = verifyManager.GetBlockLocations();
        foreach (var kvp in compactedLocations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            if (!readResult.IsSuccess) continue;

            switch (readResult.Value.Type)
            {
                case BlockType.BTreeLeaf:
                    compactedLeafCount++;
                    compactedLeafBytes += kvp.Value.Length;
                    break;
                case BlockType.BTreeInternal:
                    compactedInternalCount++;
                    compactedInternalBytes += kvp.Value.Length;
                    break;
                case BlockType.IndexRoot:
                    compactedIndexRootCount++;
                    compactedIndexRootBytes += kvp.Value.Length;
                    break;
            }
        }

        long originalTotalBTreeBytes = originalLeafBytes + originalInternalBytes + originalIndexRootBytes;
        long compactedTotalBTreeBytes = compactedLeafBytes + compactedInternalBytes + compactedIndexRootBytes;

        _output.WriteLine("");
        _output.WriteLine("=== Per-Block-Type Metrics ===");
        _output.WriteLine("");
        _output.WriteLine(string.Format("  {0,-14} {1,10} {2,12} {3,10} {4,12}",
            "Block Type", "Orig Cnt", "Orig Bytes", "Comp Cnt", "Comp Bytes"));
        _output.WriteLine("  " + new string('-', 60));
        _output.WriteLine(string.Format("  {0,-14} {1,10} {2,12:N0} {3,10} {4,12:N0}",
            "Leaf", originalLeafCount, originalLeafBytes, compactedLeafCount, compactedLeafBytes));
        _output.WriteLine(string.Format("  {0,-14} {1,10} {2,12:N0} {3,10} {4,12:N0}",
            "Internal", originalInternalCount, originalInternalBytes, compactedInternalCount, compactedInternalBytes));
        _output.WriteLine(string.Format("  {0,-14} {1,10} {2,12:N0} {3,10} {4,12:N0}",
            "IndexRoot", originalIndexRootCount, originalIndexRootBytes, compactedIndexRootCount, compactedIndexRootBytes));
        _output.WriteLine("  " + new string('-', 60));
        _output.WriteLine(string.Format("  {0,-14} {1,10} {2,12:N0} {3,10} {4,12:N0}",
            "TOTAL",
            originalLeafCount + originalInternalCount + originalIndexRootCount, originalTotalBTreeBytes,
            compactedLeafCount + compactedInternalCount + compactedIndexRootCount, compactedTotalBTreeBytes));
        _output.WriteLine($"  BTree byte reduction: {100.0 * (originalTotalBTreeBytes - compactedTotalBTreeBytes) / originalTotalBTreeBytes:F1}%");
        _output.WriteLine("");

        // Per-block-type metrics are reportable and show reduction
        Assert.True(originalLeafCount > 0, "Leaf count metric must be available");
        Assert.True(originalInternalCount > 0, "Internal node count metric must be available");
        Assert.True(originalIndexRootCount > 0, "IndexRoot count metric must be available");

        Assert.True(compactedLeafCount <= originalLeafCount,
            $"Compacted leaf count ({compactedLeafCount}) should be <= original ({originalLeafCount})");
        Assert.True(compactedInternalCount <= originalInternalCount,
            $"Compacted internal count ({compactedInternalCount}) should be <= original ({originalInternalCount})");
        Assert.Equal(1, compactedIndexRootCount); // Fresh compaction has exactly one IndexRoot

        // Total BTree bytes must be reduced
        Assert.True(compactedTotalBTreeBytes < originalTotalBTreeBytes,
            $"Total BTree bytes after compaction ({compactedTotalBTreeBytes}) should be less " +
            $"than before ({originalTotalBTreeBytes})");
    }

    [Fact]
    public async Task Compaction_SpaceAmplificationRatio_MatchesManualCalculation()
    {
        var filePath = Path.Combine(_tempDir, "amp_ratio_manual.emdb");
        var compactedPath = Path.Combine(_tempDir, "amp_ratio_manual_compacted.emdb");

        long originalFileSize;

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
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        // Verify the amplification ratio is mathematically consistent
        double spaceAmplificationRatio = (double)originalFileSize / compactedFileSize;
        long spaceReclaimedBytes = originalFileSize - compactedFileSize;
        double reductionPercent = 100.0 * spaceReclaimedBytes / originalFileSize;

        // ratio = 1 / (1 - reductionPercent/100)
        double expectedRatio = 1.0 / (1.0 - reductionPercent / 100.0);

        _output.WriteLine("");
        _output.WriteLine("=== Mathematical Consistency Check ===");
        _output.WriteLine($"  Original file size     : {originalFileSize:N0} bytes");
        _output.WriteLine($"  Compacted file size    : {compactedFileSize:N0} bytes");
        _output.WriteLine($"  Reduction              : {reductionPercent:F2}%");
        _output.WriteLine($"  Computed ratio         : {spaceAmplificationRatio:F4}x");
        _output.WriteLine($"  Expected (1/(1-r/100)) : {expectedRatio:F4}x");
        _output.WriteLine($"  Delta                  : {Math.Abs(spaceAmplificationRatio - expectedRatio):E4}");
        _output.WriteLine("");

        Assert.True(Math.Abs(spaceAmplificationRatio - expectedRatio) < 0.001,
            $"Space amplification ratio ({spaceAmplificationRatio:F4}) should match " +
            $"manual calculation ({expectedRatio:F4}) from reduction percent ({reductionPercent:F2}%)");
    }

    [Fact]
    public async Task Compaction_DeadBlockMetrics_AreAccurate()
    {
        var filePath = Path.Combine(_tempDir, "dead_metrics.emdb");
        var compactedPath = Path.Combine(_tempDir, "dead_metrics_compacted.emdb");

        int deadBlockCount;
        long deadBlockBytes;
        int liveBlockCount;
        long liveBlockBytes;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 10; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            var allPositions = await CollectAllBTreeBlockPositions(rawBlockManager);
            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            var locations = rawBlockManager.GetBlockLocations();

            deadBlockCount = 0;
            deadBlockBytes = 0;
            liveBlockCount = 0;
            liveBlockBytes = 0;

            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (!readResult.IsSuccess) continue;
                if (readResult.Value.Type != BlockType.BTreeLeaf &&
                    readResult.Value.Type != BlockType.BTreeInternal &&
                    readResult.Value.Type != BlockType.IndexRoot)
                    continue;

                if (livePositions.Contains(kvp.Value.Position))
                {
                    liveBlockCount++;
                    liveBlockBytes += kvp.Value.Length;
                }
                else
                {
                    deadBlockCount++;
                    deadBlockBytes += kvp.Value.Length;
                }
            }

            Assert.True(deadBlockCount > 0, "Test requires dead blocks to be meaningful");

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        // Verify dead block metrics are consistent
        Assert.Equal(deadBlockCount + liveBlockCount,
            (await CollectAllBTreeBlockPositionsFromFile(filePath)));

        // Verify compacted file has zero dead blocks
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        var compactedAll = await CollectAllBTreeBlockPositions(verifyManager);
        var compactedLive = await CollectLiveNodePositions(verifyManager);

        _output.WriteLine("");
        _output.WriteLine("=== Dead Block Metrics ===");
        _output.WriteLine($"  WITHOUT compaction:");
        _output.WriteLine($"    Live blocks  : {liveBlockCount} ({liveBlockBytes:N0} bytes)");
        _output.WriteLine($"    Dead blocks  : {deadBlockCount} ({deadBlockBytes:N0} bytes)");
        _output.WriteLine($"    Total blocks : {liveBlockCount + deadBlockCount}");
        _output.WriteLine($"  WITH compaction:");
        _output.WriteLine($"    Live blocks  : {compactedLive.Count}");
        _output.WriteLine($"    Dead blocks  : {compactedAll.Count - compactedLive.Count}");
        _output.WriteLine($"    Total blocks : {compactedAll.Count}");
        _output.WriteLine("");

        Assert.Equal(compactedAll.Count, compactedLive.Count);
    }

    [Fact]
    public async Task Compaction_ThreeLevelTree_SpaceAmplificationMetrics()
    {
        var filePath = Path.Combine(_tempDir, "3level_amp.emdb");
        var compactedPath = Path.Combine(_tempDir, "3level_amp_compacted.emdb");

        long originalFileSize;
        int originalTotalBTreeBlocks;
        int originalLiveBTreeBlocks;

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
            originalTotalBTreeBlocks = (await CollectAllBTreeBlockPositions(rawBlockManager)).Count;
            originalLiveBTreeBlocks = (await CollectLiveNodePositions(rawBlockManager)).Count;

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        // Compute all metrics
        double spaceAmplificationRatio = (double)originalFileSize / compactedFileSize;
        long spaceReclaimedBytes = originalFileSize - compactedFileSize;
        double reductionPercent = 100.0 * spaceReclaimedBytes / originalFileSize;
        int deadBlocksBefore = originalTotalBTreeBlocks - originalLiveBTreeBlocks;

        _output.WriteLine("");
        _output.WriteLine("=== 3-Level Tree Space Amplification ===");
        _output.WriteLine("");
        _output.WriteLine("  WITHOUT Compaction:");
        _output.WriteLine($"    File size            : {originalFileSize:N0} bytes ({originalFileSize / 1024.0:F1} KB)");
        _output.WriteLine($"    Total BTree blocks   : {originalTotalBTreeBlocks}");
        _output.WriteLine($"    Live BTree blocks    : {originalLiveBTreeBlocks}");
        _output.WriteLine($"    Dead BTree blocks    : {deadBlocksBefore}");
        _output.WriteLine($"    Dead block ratio     : {100.0 * deadBlocksBefore / originalTotalBTreeBlocks:F1}%");
        _output.WriteLine("");
        _output.WriteLine("  WITH Compaction:");
        _output.WriteLine($"    File size            : {compactedFileSize:N0} bytes ({compactedFileSize / 1024.0:F1} KB)");
        _output.WriteLine($"    Space reclaimed      : {spaceReclaimedBytes:N0} bytes ({reductionPercent:F1}%)");
        _output.WriteLine($"    Amplification ratio  : {spaceAmplificationRatio:F3}x");
        _output.WriteLine("");

        // 3-level trees accumulate significant dead blocks from splits
        Assert.True(spaceAmplificationRatio > 1.0,
            $"3-level tree space amplification ratio should be > 1.0, got {spaceAmplificationRatio:F3}");
        Assert.True(reductionPercent > 10,
            $"3-level tree should show >10% space reduction, got {reductionPercent:F1}%");
        Assert.True(deadBlocksBefore > 0,
            $"3-level tree should have dead blocks before compaction, got {deadBlocksBefore}");
        Assert.True(spaceReclaimedBytes > 0,
            $"Space reclaimed must be positive for 3-level tree, got {spaceReclaimedBytes}");
    }

    [Fact]
    public async Task Compaction_SpaceUtilization_ImprovesAfterCompaction()
    {
        var filePath = Path.Combine(_tempDir, "utilization.emdb");
        var compactedPath = Path.Combine(_tempDir, "utilization_compacted.emdb");

        long originalFileSize;
        long originalLiveBTreeBytes;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < BTreeLeafNode.MaxEntries + 20; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            for (int i = 1; i <= 8; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                var dr = await btreeIndex.DeleteAsync(key);
                Assert.True(dr.IsSuccess, $"Delete {i} failed: {dr.Error}");
            }

            originalFileSize = rawBlockManager.FileLength;

            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            var locations = rawBlockManager.GetBlockLocations();
            originalLiveBTreeBytes = 0;
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess &&
                    (readResult.Value.Type == BlockType.BTreeLeaf ||
                     readResult.Value.Type == BlockType.BTreeInternal ||
                     readResult.Value.Type == BlockType.IndexRoot) &&
                    livePositions.Contains(kvp.Value.Position))
                {
                    originalLiveBTreeBytes += kvp.Value.Length;
                }
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        // Space utilization = live BTree bytes / total file size
        // Before compaction, dead blocks reduce utilization
        double originalUtilization = (double)originalLiveBTreeBytes / originalFileSize;

        // After compaction, all BTree blocks are live, so utilization improves
        using var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false);
        long compactedBTreeBytes = 0;
        var compactedLocations = verifyManager.GetBlockLocations();
        foreach (var kvp in compactedLocations)
        {
            var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess &&
                (readResult.Value.Type == BlockType.BTreeLeaf ||
                 readResult.Value.Type == BlockType.BTreeInternal ||
                 readResult.Value.Type == BlockType.IndexRoot))
            {
                compactedBTreeBytes += kvp.Value.Length;
            }
        }

        double compactedUtilization = (double)compactedBTreeBytes / compactedFileSize;

        _output.WriteLine("");
        _output.WriteLine("=== Space Utilization (insert + delete workload) ===");
        _output.WriteLine($"  WITHOUT compaction utilization : {originalUtilization:P2} (live: {originalLiveBTreeBytes:N0} / total: {originalFileSize:N0})");
        _output.WriteLine($"  WITH compaction utilization    : {compactedUtilization:P2} (live: {compactedBTreeBytes:N0} / total: {compactedFileSize:N0})");
        _output.WriteLine($"  Utilization improvement        : {(compactedUtilization - originalUtilization):P2}");
        _output.WriteLine("");

        Assert.True(compactedUtilization > originalUtilization,
            $"Space utilization should improve after compaction. " +
            $"Before: {originalUtilization:P2}, After: {compactedUtilization:P2}");
    }

    /// <summary>
    /// Comprehensive summary: documents space amplification metrics at multiple scale
    /// points, comparing WITHOUT compaction vs WITH compaction in a single table.
    /// </summary>
    [Fact]
    public async Task SpaceAmplification_WithAndWithoutCompaction_Summary()
    {
        var scalePoints = new[] { 100, 500, 2000 };
        var results = new List<SpaceAmplificationResult>();

        foreach (var count in scalePoints)
        {
            BlockIdGenerator.Instance.Reset();
            var result = await MeasureSpaceAmplification(count, deletePercent: 20);
            results.Add(result);
        }

        _output.WriteLine("");
        _output.WriteLine("=== Space Amplification Metrics: With and Without Compaction ===");
        _output.WriteLine("");
        _output.WriteLine(string.Format("  {0,-8} {1,14} {2,14} {3,10} {4,8} {5,8} {6,10}",
            "Entries", "Without (B)", "With (B)", "Ratio", "Dead%", "Recl%", "Util Impr"));
        _output.WriteLine("  " + new string('-', 78));

        foreach (var r in results)
        {
            _output.WriteLine(string.Format("  {0,-8} {1,14:N0} {2,14:N0} {3,10:F3}x {4,7:F1}% {5,7:F1}% {6,9:F1}%",
                r.EntryCount,
                r.OriginalFileSize,
                r.CompactedFileSize,
                r.SpaceAmplificationRatio,
                r.DeadBlockPercent,
                r.ReductionPercent,
                (r.CompactedUtilization - r.OriginalUtilization) * 100));
        }

        _output.WriteLine("");
        _output.WriteLine("  Legend:");
        _output.WriteLine("    Without (B)  = File size before compaction (bytes)");
        _output.WriteLine("    With (B)     = File size after compaction (bytes)");
        _output.WriteLine("    Ratio        = Space amplification ratio (original / compacted)");
        _output.WriteLine("    Dead%        = Percentage of BTree blocks that are dead before compaction");
        _output.WriteLine("    Recl%        = Percentage of file size reclaimed by compaction");
        _output.WriteLine("    Util Impr    = Utilization improvement (compacted - original)");
        _output.WriteLine("");
        _output.WriteLine($"  Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        _output.WriteLine($"  Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        _output.WriteLine("");

        // Verify all scale points show meaningful space amplification
        foreach (var r in results)
        {
            Assert.True(r.SpaceAmplificationRatio > 1.0,
                $"At {r.EntryCount} entries, ratio should be > 1.0, got {r.SpaceAmplificationRatio:F3}");
            Assert.True(r.CompactedUtilization >= r.OriginalUtilization,
                $"At {r.EntryCount} entries, utilization should improve after compaction");
        }
    }

    private async Task<SpaceAmplificationResult> MeasureSpaceAmplification(int entryCount, int deletePercent)
    {
        var filePath = Path.Combine(_tempDir, $"amp_{entryCount}.emdb");
        var compactedPath = Path.Combine(_tempDir, $"amp_{entryCount}_compacted.emdb");

        long originalFileSize;
        int totalBTreeBlocks, liveBTreeBlocks;
        long liveBTreeBytes;

        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            for (int i = 0; i < entryCount; i++)
            {
                var key = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
                var r = await btreeIndex.InsertAsync(key, i * 100L, i);
                Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            }

            int deleteCount = entryCount * deletePercent / 100;
            for (int i = 1; i <= deleteCount; i++)
            {
                var key = new EmailHashedID((ulong)i, 0, 0, 0);
                await btreeIndex.DeleteAsync(key);
            }

            originalFileSize = rawBlockManager.FileLength;

            var allPositions = await CollectAllBTreeBlockPositions(rawBlockManager);
            var livePositions = await CollectLiveNodePositions(rawBlockManager);
            totalBTreeBlocks = allPositions.Count;
            liveBTreeBlocks = livePositions.Count;

            var locations = rawBlockManager.GetBlockLocations();
            liveBTreeBytes = 0;
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess &&
                    (readResult.Value.Type == BlockType.BTreeLeaf ||
                     readResult.Value.Type == BlockType.BTreeInternal ||
                     readResult.Value.Type == BlockType.IndexRoot) &&
                    livePositions.Contains(kvp.Value.Position))
                {
                    liveBTreeBytes += kvp.Value.Length;
                }
            }

            BlockIdGenerator.Instance.Reset();
            await CompactLiveTreeToFile(rawBlockManager, compactedPath);
        }

        long compactedFileSize = new FileInfo(compactedPath).Length;

        // Compute compacted utilization
        long compactedBTreeBytes = 0;
        using (var verifyManager = new RawBlockManager(compactedPath, createIfNotExists: false))
        {
            var compactedLocations = verifyManager.GetBlockLocations();
            foreach (var kvp in compactedLocations)
            {
                var readResult = await verifyManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess &&
                    (readResult.Value.Type == BlockType.BTreeLeaf ||
                     readResult.Value.Type == BlockType.BTreeInternal ||
                     readResult.Value.Type == BlockType.IndexRoot))
                {
                    compactedBTreeBytes += kvp.Value.Length;
                }
            }
        }

        int deadBlocks = totalBTreeBlocks - liveBTreeBlocks;
        return new SpaceAmplificationResult
        {
            EntryCount = entryCount,
            OriginalFileSize = originalFileSize,
            CompactedFileSize = compactedFileSize,
            SpaceAmplificationRatio = (double)originalFileSize / compactedFileSize,
            ReductionPercent = 100.0 * (originalFileSize - compactedFileSize) / originalFileSize,
            DeadBlockPercent = totalBTreeBlocks > 0 ? 100.0 * deadBlocks / totalBTreeBlocks : 0,
            OriginalUtilization = originalFileSize > 0 ? (double)liveBTreeBytes / originalFileSize : 0,
            CompactedUtilization = compactedFileSize > 0 ? (double)compactedBTreeBytes / compactedFileSize : 0
        };
    }

    private class SpaceAmplificationResult
    {
        public int EntryCount { get; set; }
        public long OriginalFileSize { get; set; }
        public long CompactedFileSize { get; set; }
        public double SpaceAmplificationRatio { get; set; }
        public double ReductionPercent { get; set; }
        public double DeadBlockPercent { get; set; }
        public double OriginalUtilization { get; set; }
        public double CompactedUtilization { get; set; }
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

    private async Task<int> CollectAllBTreeBlockPositionsFromFile(string filePath)
    {
        using var manager = new RawBlockManager(filePath, createIfNotExists: false);
        var positions = await CollectAllBTreeBlockPositions(manager);
        return positions.Count;
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
