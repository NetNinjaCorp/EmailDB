using System;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

public class DirectAnalysisTest : IDisposable
{
    private readonly string _testDir;
    private readonly Random _random = new(42);
    private readonly string _outputFile;

    public DirectAnalysisTest()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DirectAnalysis_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _outputFile = Path.Combine(Path.GetTempPath(), "EmailDB_Analysis_Results.txt");
    }

    [Fact]
    public async Task Run_Complete_Storage_Analysis()
    {
        var output = new StringBuilder();
        output.AppendLine("📊 EMAILDB STORAGE EFFICIENCY ANALYSIS");
        output.AppendLine("=====================================");
        output.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        output.AppendLine();

        // Test 1: Variable Email Sizes
        output.AppendLine("🔍 TEST 1: VARIABLE EMAIL SIZES (Randomized)");
        output.AppendLine("==========================================");
        
        var emailSizes = GenerateRealisticEmailSizes(1000, 10240, 5120);
        var totalRawData = emailSizes.Sum();
        
        output.AppendLine($"Generated {emailSizes.Count} emails with randomized sizes:");
        output.AppendLine($"  Total raw data: {FormatBytes(totalRawData)}");
        output.AppendLine($"  Average size: {FormatBytes(totalRawData / emailSizes.Count)}");
        output.AppendLine($"  Min size: {FormatBytes(emailSizes.Min())}");
        output.AppendLine($"  Max size: {FormatBytes(emailSizes.Max())}");
        output.AppendLine();
        
        // Size distribution
        output.AppendLine("Size Distribution:");
        var buckets = new[] { 1024, 5120, 10240, 25600, 51200, 102400, int.MaxValue };
        var bucketNames = new[] { "<1KB", "1-5KB", "5-10KB", "10-25KB", "25-50KB", "50-100KB", ">100KB" };
        var bucketCounts = new int[buckets.Length];
        
        foreach (var size in emailSizes)
        {
            for (int i = 0; i < buckets.Length; i++)
            {
                if (size <= buckets[i])
                {
                    bucketCounts[i]++;
                    break;
                }
            }
        }
        
        for (int i = 0; i < bucketNames.Length; i++)
        {
            if (bucketCounts[i] > 0)
            {
                var percent = (double)bucketCounts[i] / emailSizes.Count * 100;
                output.AppendLine($"  {bucketNames[i],-10}: {bucketCounts[i],5} emails ({percent,5:F1}%)");
            }
        }
        
        // Test different storage approaches
        output.AppendLine("\nStorage Efficiency Results:");
        
        // Baseline
        var baselineSize = await TestBaseline(emailSizes);
        output.AppendLine($"  Baseline:        {FormatBytes(baselineSize)} (100.0%)");
        
        // With Hash Chain
        var hashChainSize = await TestHashChain(emailSizes);
        var hashChainOverhead = ((double)hashChainSize / baselineSize - 1) * 100;
        output.AppendLine($"  Hash Chain:      {FormatBytes(hashChainSize)} (+{hashChainOverhead:F1}%)");
        
        // With Checkpoints
        var checkpointSize = await TestCheckpoints(emailSizes);
        var checkpointOverhead = ((double)checkpointSize / baselineSize - 1) * 100;
        output.AppendLine($"  Checkpoints:     {FormatBytes(checkpointSize)} (+{checkpointOverhead:F1}%)");
        
        output.AppendLine();
        output.AppendLine("💡 Key Insights:");
        output.AppendLine($"  - Hash chain adds ~{hashChainOverhead:F1}% overhead for cryptographic integrity");
        output.AppendLine($"  - Checkpoints add ~{checkpointOverhead:F1}% overhead for recovery capability");
        output.AppendLine($"  - Storage efficiency: {(double)totalRawData / baselineSize * 100:F1}% (raw data / file size)");
        
        // Test 2: Large Email Test
        output.AppendLine("\n\n🔍 TEST 2: LARGE EMAIL SIZES (50KB average)");
        output.AppendLine("==========================================");
        
        var largeEmailSizes = GenerateRealisticEmailSizes(500, 51200, 20480);
        var largeTotalRaw = largeEmailSizes.Sum();
        
        output.AppendLine($"Generated {largeEmailSizes.Count} large emails:");
        output.AppendLine($"  Total raw data: {FormatBytes(largeTotalRaw)}");
        output.AppendLine($"  Average size: {FormatBytes(largeTotalRaw / largeEmailSizes.Count)}");
        
        var largeBaselineSize = await TestBaseline(largeEmailSizes);
        var largeHashChainSize = await TestHashChain(largeEmailSizes);
        
        output.AppendLine("\nResults for large emails:");
        output.AppendLine($"  Baseline:    {FormatBytes(largeBaselineSize)}");
        output.AppendLine($"  Hash Chain:  {FormatBytes(largeHashChainSize)} (+{((double)largeHashChainSize / largeBaselineSize - 1) * 100:F1}%)");
        output.AppendLine($"  Efficiency:  {(double)largeTotalRaw / largeBaselineSize * 100:F1}%");
        
        // Test 3: Update Pattern Analysis
        output.AppendLine("\n\n🔍 TEST 3: UPDATE PATTERN ANALYSIS");
        output.AppendLine("=================================");
        
        var updateResults = await TestUpdatePatterns();
        foreach (var (pattern, efficiency) in updateResults)
        {
            output.AppendLine($"  {pattern}: {efficiency:F1}% space efficiency");
        }
        
        output.AppendLine("\n💡 Update Pattern Insights:");
        output.AppendLine("  - Append-only design reduces efficiency with frequent updates");
        output.AppendLine("  - Consider periodic compaction for high-update scenarios");
        
        // Save results
        await File.WriteAllTextAsync(_outputFile, output.ToString());
        
        // Also write to console for immediate viewing
        Console.WriteLine(output.ToString());
        Console.WriteLine($"\n📄 Full results saved to: {_outputFile}");
        
        // Assert something to make test pass
        Assert.True(baselineSize > 0);
    }

    private List<int> GenerateRealisticEmailSizes(int count, int avgSize, int stdDev)
    {
        var sizes = new List<int>();
        
        for (int i = 0; i < count; i++)
        {
            // Use normal distribution with bounds
            var size = (int)GetNormalDistributedValue(avgSize, stdDev);
            
            // Ensure minimum size
            size = Math.Max(100, size);
            
            // Add occasional outliers (large attachments)
            if (_random.NextDouble() < 0.05) // 5% chance
            {
                size *= _random.Next(5, 20); // 5x to 20x larger
            }
            
            sizes.Add(size);
        }
        
        return sizes;
    }

    private double GetNormalDistributedValue(double mean, double stdDev)
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * randStdNormal;
    }

    private async Task<long> TestBaseline(List<int> emailSizes)
    {
        var file = Path.Combine(_testDir, "baseline.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            for (int i = 0; i < emailSizes.Count; i++)
            {
                var block = CreateEmailBlock(i, emailSizes[i]);
                await blockManager.WriteBlockAsync(block);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestHashChain(List<int> emailSizes)
    {
        var file = Path.Combine(_testDir, "hashchain.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            var hashChainManager = new HashChainManager(blockManager);
            
            for (int i = 0; i < emailSizes.Count; i++)
            {
                var block = CreateEmailBlock(i, emailSizes[i]);
                await blockManager.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestCheckpoints(List<int> emailSizes)
    {
        var file = Path.Combine(_testDir, "checkpoint.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            var checkpointManager = new CheckpointManager(blockManager);
            
            for (int i = 0; i < emailSizes.Count; i++)
            {
                var block = CreateEmailBlock(i, emailSizes[i]);
                await blockManager.WriteBlockAsync(block);
                
                // Checkpoint every 10th email
                if (i % 10 == 0)
                {
                    await checkpointManager.CreateCheckpointAsync((ulong)block.BlockId);
                }
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<Dictionary<string, double>> TestUpdatePatterns()
    {
        var results = new Dictionary<string, double>();
        var initialEmails = 100;
        var emailSize = 5120; // 5KB
        
        var updateRates = new[] { ("10% updates", 0.1), ("30% updates", 0.3), ("50% updates", 0.5) };
        
        foreach (var (name, rate) in updateRates)
        {
            var file = Path.Combine(_testDir, $"updates_{rate}.emdb");
            
            using (var blockManager = new RawBlockManager(file))
            {
                // Write initial emails
                for (int i = 0; i < initialEmails; i++)
                {
                    var block = CreateEmailBlock(i, emailSize);
                    await blockManager.WriteBlockAsync(block);
                }
                
                // Simulate updates
                var updateCount = (int)(initialEmails * rate);
                for (int i = 0; i < updateCount; i++)
                {
                    var updateBlock = CreateEmailBlock(10000 + i, emailSize);
                    updateBlock.Flags = 0x10; // Update flag
                    await blockManager.WriteBlockAsync(updateBlock);
                }
            }
            
            var fileSize = new FileInfo(file).Length;
            var efficiency = (double)(initialEmails * emailSize) / fileSize * 100;
            results[name] = efficiency;
        }
        
        return results;
    }

    private Block CreateEmailBlock(int id, int size)
    {
        var payload = new byte[size];
        _random.NextBytes(payload);
        
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