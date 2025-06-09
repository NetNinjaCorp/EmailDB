using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Simple analysis of storage overhead for different approaches.
/// </summary>
public class SimpleStorageAnalysisTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;

    public SimpleStorageAnalysisTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"StorageAnalysis_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Compare_Storage_Overhead_Simple()
    {
        _output.WriteLine("📊 STORAGE OVERHEAD COMPARISON");
        _output.WriteLine("=============================\n");

        const int blockCount = 100;
        const int blockSize = 1024; // 1KB blocks
        
        // Test 1: Baseline
        var baselineFile = Path.Combine(_testDir, "baseline.emdb");
        long baselineSize = 0;
        
        using (var blockManager = new RawBlockManager(baselineFile))
        {
            for (int i = 0; i < blockCount; i++)
            {
                var block = CreateTestBlock(i, blockSize);
                await blockManager.WriteBlockAsync(block);
            }
        }
        
        baselineSize = new FileInfo(baselineFile).Length;
        _output.WriteLine($"📁 BASELINE (No extras):");
        _output.WriteLine($"   Blocks: {blockCount}");
        _output.WriteLine($"   Raw data: {FormatBytes(blockCount * blockSize)}");
        _output.WriteLine($"   File size: {FormatBytes(baselineSize)}");
        _output.WriteLine($"   Overhead: {FormatBytes(baselineSize - blockCount * blockSize)} ({GetOverheadPercent(baselineSize, blockCount * blockSize):F1}%)");
        
        // Test 2: With Hash Chain
        var hashChainFile = Path.Combine(_testDir, "hashchain.emdb");
        long hashChainSize = 0;
        
        using (var blockManager = new RawBlockManager(hashChainFile))
        {
            var hashChainManager = new HashChainManager(blockManager);
            
            for (int i = 0; i < blockCount; i++)
            {
                var block = CreateTestBlock(i, blockSize);
                await blockManager.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
            }
        }
        
        hashChainSize = new FileInfo(hashChainFile).Length;
        _output.WriteLine($"\n🔗 WITH HASH CHAIN:");
        _output.WriteLine($"   File size: {FormatBytes(hashChainSize)}");
        _output.WriteLine($"   Extra overhead: {FormatBytes(hashChainSize - baselineSize)} ({GetOverheadPercent(hashChainSize, baselineSize):F1}% increase)");
        _output.WriteLine($"   Per-block overhead: {FormatBytes((hashChainSize - baselineSize) / blockCount)}");
        
        // Test 3: With Checkpoints (20% of blocks)
        var checkpointFile = Path.Combine(_testDir, "checkpoint.emdb");
        long checkpointSize = 0;
        int checkpointCount = blockCount / 5; // 20%
        
        using (var blockManager = new RawBlockManager(checkpointFile))
        {
            var checkpointManager = new CheckpointManager(blockManager);
            
            for (int i = 0; i < blockCount; i++)
            {
                var block = CreateTestBlock(i, blockSize);
                await blockManager.WriteBlockAsync(block);
                
                // Checkpoint every 5th block
                if (i % 5 == 0)
                {
                    await checkpointManager.CreateCheckpointAsync((ulong)block.BlockId);
                }
            }
        }
        
        checkpointSize = new FileInfo(checkpointFile).Length;
        _output.WriteLine($"\n💾 WITH CHECKPOINTS (20% of blocks):");
        _output.WriteLine($"   File size: {FormatBytes(checkpointSize)}");
        _output.WriteLine($"   Extra overhead: {FormatBytes(checkpointSize - baselineSize)} ({GetOverheadPercent(checkpointSize, baselineSize):F1}% increase)");
        _output.WriteLine($"   Checkpointed blocks: {checkpointCount}");
        
        // Test 4: Combined
        var combinedFile = Path.Combine(_testDir, "combined.emdb");
        long combinedSize = 0;
        
        using (var blockManager = new RawBlockManager(combinedFile))
        {
            var hashChainManager = new HashChainManager(blockManager);
            var checkpointManager = new CheckpointManager(blockManager);
            
            for (int i = 0; i < blockCount; i++)
            {
                var block = CreateTestBlock(i, blockSize);
                await blockManager.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
                
                if (i % 5 == 0)
                {
                    await checkpointManager.CreateCheckpointAsync((ulong)block.BlockId);
                }
            }
        }
        
        combinedSize = new FileInfo(combinedFile).Length;
        _output.WriteLine($"\n🔄 COMBINED (Hash Chain + Checkpoints):");
        _output.WriteLine($"   File size: {FormatBytes(combinedSize)}");
        _output.WriteLine($"   Extra overhead: {FormatBytes(combinedSize - baselineSize)} ({GetOverheadPercent(combinedSize, baselineSize):F1}% increase)");
        
        // Summary
        _output.WriteLine($"\n📊 SUMMARY:");
        _output.WriteLine($"   Baseline:    {FormatBytes(baselineSize)} (100%)");
        _output.WriteLine($"   Hash Chain:  {FormatBytes(hashChainSize)} (+{GetOverheadPercent(hashChainSize, baselineSize):F1}%)");
        _output.WriteLine($"   Checkpoints: {FormatBytes(checkpointSize)} (+{GetOverheadPercent(checkpointSize, baselineSize):F1}%)");
        _output.WriteLine($"   Combined:    {FormatBytes(combinedSize)} (+{GetOverheadPercent(combinedSize, baselineSize):F1}%)");
        
        // WAL Analysis (theoretical)
        _output.WriteLine($"\n📝 WAL ANALYSIS (Theoretical):");
        var walOverhead = blockCount * 32; // Assume 32 bytes per WAL entry
        _output.WriteLine($"   Estimated WAL size: {FormatBytes(walOverhead)}");
        _output.WriteLine($"   As % of data: {GetOverheadPercent(walOverhead, blockCount * blockSize):F1}%");
        _output.WriteLine($"   Total with WAL: {FormatBytes(baselineSize + walOverhead)}");
        
        // Recommendations
        _output.WriteLine($"\n💡 RECOMMENDATIONS:");
        if (hashChainSize - baselineSize < checkpointSize - baselineSize)
        {
            _output.WriteLine($"   ✅ Hash Chain has lower overhead than Checkpoints");
            _output.WriteLine($"      Good for: Archival, integrity verification");
        }
        else
        {
            _output.WriteLine($"   ✅ Checkpoints have lower overhead than Hash Chain");
            _output.WriteLine($"      Good for: Recovery, selective backup");
        }
        
        _output.WriteLine($"\n   📌 For archival: Use Hash Chain (only {GetOverheadPercent(hashChainSize, baselineSize):F1}% overhead)");
        _output.WriteLine($"   📌 For recovery: Use selective Checkpoints");
        _output.WriteLine($"   📌 For transactions: Use WAL (lowest overhead but requires management)");
    }

    [Fact]
    public async Task Analyze_Update_Patterns()
    {
        _output.WriteLine("📈 UPDATE PATTERN ANALYSIS");
        _output.WriteLine("========================\n");

        const int initialBlocks = 100;
        const int blockSize = 1024;
        
        // Simulate different update patterns
        var patterns = new[]
        {
            ("10% updates", 0.1),
            ("25% updates", 0.25),
            ("50% updates", 0.5),
            ("90% updates", 0.9)
        };

        foreach (var (name, updateRatio) in patterns)
        {
            _output.WriteLine($"\n📊 Pattern: {name}");
            _output.WriteLine("-------------------");
            
            var file = Path.Combine(_testDir, $"updates_{updateRatio}.emdb");
            
            using (var blockManager = new RawBlockManager(file))
            {
                // Write initial blocks
                for (int i = 0; i < initialBlocks; i++)
                {
                    var block = CreateTestBlock(i, blockSize);
                    await blockManager.WriteBlockAsync(block);
                }
                
                // Simulate updates (append-only)
                var updateCount = (int)(initialBlocks * updateRatio);
                for (int i = 0; i < updateCount; i++)
                {
                    var updateBlock = CreateTestBlock(10000 + i, blockSize);
                    updateBlock.Flags = 0x10; // Update flag
                    await blockManager.WriteBlockAsync(updateBlock);
                }
            }
            
            var fileSize = new FileInfo(file).Length;
            var expectedSize = (initialBlocks + (int)(initialBlocks * updateRatio)) * (blockSize + 61);
            var efficiency = (double)(initialBlocks * blockSize) / fileSize * 100;
            
            _output.WriteLine($"   Total blocks: {initialBlocks + (int)(initialBlocks * updateRatio)}");
            _output.WriteLine($"   File size: {FormatBytes(fileSize)}");
            _output.WriteLine($"   Space efficiency: {efficiency:F1}%");
            _output.WriteLine($"   Wasted space: {FormatBytes(fileSize - initialBlocks * blockSize)}");
        }

        _output.WriteLine($"\n💡 INSIGHTS:");
        _output.WriteLine($"   - Space efficiency decreases with more updates");
        _output.WriteLine($"   - Append-only design trades space for simplicity/safety");
        _output.WriteLine($"   - Regular compaction recommended for high-update scenarios");
    }

    private Block CreateTestBlock(int id, int payloadSize)
    {
        var payload = new byte[payloadSize];
        Array.Fill(payload, (byte)(id % 256));
        
        return new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = id,
            Payload = payload
        };
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:F2} {sizes[order]}";
    }

    private double GetOverheadPercent(long actual, long baseline)
    {
        return ((double)actual / baseline - 1) * 100;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best effort
        }
    }
}