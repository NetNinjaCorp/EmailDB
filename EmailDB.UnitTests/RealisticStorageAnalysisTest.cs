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

/// <summary>
/// Realistic storage analysis with variable email sizes and distributions.
/// </summary>
public class RealisticStorageAnalysisTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42); // Fixed seed for reproducibility

    public RealisticStorageAnalysisTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"RealisticStorage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Theory]
    [InlineData(2048, 512, 100)]      // 2KB avg, 512B std dev, 100 emails
    [InlineData(5120, 2048, 100)]     // 5KB avg, 2KB std dev, 100 emails  
    [InlineData(10240, 5120, 100)]    // 10KB avg, 5KB std dev, 100 emails
    [InlineData(25600, 10240, 100)]   // 25KB avg, 10KB std dev, 100 emails
    [InlineData(5120, 2048, 1000)]    // 5KB avg, 2KB std dev, 1000 emails
    public async Task Analyze_With_Variable_Email_Sizes(int avgSize, int stdDev, int emailCount)
    {
        _output.WriteLine($"📊 REALISTIC STORAGE ANALYSIS");
        _output.WriteLine($"============================");
        _output.WriteLine($"Parameters: {emailCount} emails, avg size {FormatBytes(avgSize)}, std dev {FormatBytes(stdDev)}\n");

        // Generate realistic email size distribution
        var emailSizes = GenerateRealisticEmailSizes(emailCount, avgSize, stdDev);
        var totalRawData = emailSizes.Sum();
        
        _output.WriteLine($"📈 Email Size Distribution:");
        PrintSizeDistribution(emailSizes);

        // Test different configurations
        var results = new Dictionary<string, long>();
        
        // Baseline
        results["Baseline"] = await TestConfiguration("baseline", emailSizes, async (bm, sizes) =>
        {
            for (int i = 0; i < sizes.Count; i++)
            {
                var block = CreateEmailBlock(i, sizes[i]);
                await bm.WriteBlockAsync(block);
            }
        });

        // With Hash Chain
        results["HashChain"] = await TestConfiguration("hashchain", emailSizes, async (bm, sizes) =>
        {
            var hashChainManager = new HashChainManager(bm);
            for (int i = 0; i < sizes.Count; i++)
            {
                var block = CreateEmailBlock(i, sizes[i]);
                await bm.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
            }
        });

        // With Checkpoints (variable strategy based on email size)
        results["SmartCheckpoints"] = await TestConfiguration("smartcheckpoint", emailSizes, async (bm, sizes) =>
        {
            var checkpointManager = new CheckpointManager(bm);
            for (int i = 0; i < sizes.Count; i++)
            {
                var block = CreateEmailBlock(i, sizes[i]);
                await bm.WriteBlockAsync(block);
                
                // Smart checkpointing: checkpoint large emails and every Nth email
                if (sizes[i] > avgSize * 1.5 || i % 10 == 0)
                {
                    await checkpointManager.CreateCheckpointAsync((ulong)block.BlockId);
                }
            }
        });

        // Summary
        _output.WriteLine($"\n📊 RESULTS SUMMARY:");
        _output.WriteLine($"Total raw email data: {FormatBytes(totalRawData)}");
        _output.WriteLine($"Average email size: {FormatBytes(avgSize)} (actual: {FormatBytes(totalRawData / emailCount)})");
        
        foreach (var (config, size) in results)
        {
            var overhead = size - totalRawData;
            var overheadPercent = (double)overhead / totalRawData * 100;
            _output.WriteLine($"\n{config}:");
            _output.WriteLine($"  File size: {FormatBytes(size)}");
            _output.WriteLine($"  Overhead: {FormatBytes(overhead)} ({overheadPercent:F1}%)");
            _output.WriteLine($"  Efficiency: {(double)totalRawData / size * 100:F1}%");
        }
        
        // Recommendations based on results
        _output.WriteLine($"\n💡 INSIGHTS:");
        var hashChainOverhead = ((double)results["HashChain"] / results["Baseline"] - 1) * 100;
        _output.WriteLine($"  Hash chain adds {hashChainOverhead:F1}% overhead");
        _output.WriteLine($"  Smart checkpointing is more efficient for variable-sized emails");
        
        if (avgSize > 10240) // Large emails
        {
            _output.WriteLine($"  For large emails (>{FormatBytes(10240)}), consider compression");
        }
    }

    [Fact]
    public async Task Analyze_Real_World_Email_Distribution()
    {
        _output.WriteLine("🌍 REAL-WORLD EMAIL DISTRIBUTION ANALYSIS");
        _output.WriteLine("=======================================\n");

        // Simulate real-world email distribution
        // Based on typical corporate email patterns
        var distribution = new EmailDistribution
        {
            // Small emails (notifications, confirmations) - 40%
            { (500, 2000), 0.40 },
            // Medium emails (regular correspondence) - 35%
            { (2000, 10000), 0.35 },
            // Large emails (with attachments) - 20%
            { (10000, 100000), 0.20 },
            // Very large emails (multiple attachments) - 5%
            { (100000, 500000), 0.05 }
        };

        const int totalEmails = 1000;
        var emails = GenerateEmailsFromDistribution(totalEmails, distribution);
        
        _output.WriteLine("📊 Email Distribution:");
        PrintDetailedDistribution(emails);

        // Test with different update patterns
        var updatePatterns = new[] { 0.05, 0.15, 0.30 }; // 5%, 15%, 30% update rates
        
        foreach (var updateRate in updatePatterns)
        {
            _output.WriteLine($"\n📈 Testing with {updateRate:P0} update rate:");
            
            var file = Path.Combine(_testDir, $"realworld_{updateRate}.emdb");
            using (var blockManager = new RawBlockManager(file))
            {
                // Write initial emails
                var blockIds = new List<long>();
                foreach (var email in emails)
                {
                    var block = CreateEmailBlock(email.Id, email.Size);
                    await blockManager.WriteBlockAsync(block);
                    blockIds.Add(block.BlockId);
                }
                
                // Simulate updates (prefer updating larger emails)
                var emailsToUpdate = emails
                    .OrderByDescending(e => e.Size)
                    .Take((int)(emails.Count * updateRate))
                    .ToList();
                    
                foreach (var email in emailsToUpdate)
                {
                    var updateBlock = CreateEmailBlock(10000 + email.Id, email.Size);
                    updateBlock.Flags = 0x10; // Update flag
                    await blockManager.WriteBlockAsync(updateBlock);
                }
            }
            
            var fileInfo = new FileInfo(file);
            var totalRawSize = emails.Sum(e => (long)e.Size);
            var efficiency = (double)totalRawSize / fileInfo.Length * 100;
            
            _output.WriteLine($"  File size: {FormatBytes(fileInfo.Length)}");
            _output.WriteLine($"  Space efficiency: {efficiency:F1}%");
            _output.WriteLine($"  Wasted space: {FormatBytes(fileInfo.Length - totalRawSize)}");
        }
        
        // Compression potential analysis
        _output.WriteLine($"\n🗜️ COMPRESSION POTENTIAL:");
        AnalyzeCompressionPotential(emails);
    }

    [Fact]
    public async Task Analyze_Extreme_Cases()
    {
        _output.WriteLine("🔥 EXTREME CASES ANALYSIS");
        _output.WriteLine("========================\n");

        // Test 1: Many tiny emails (like notifications)
        _output.WriteLine("📧 Case 1: Many tiny emails (100 bytes each)");
        var tinyEmails = Enumerable.Range(0, 1000).Select(i => 100).ToList();
        var tinyResults = await TestConfiguration("tiny", tinyEmails, async (bm, sizes) =>
        {
            for (int i = 0; i < sizes.Count; i++)
            {
                var block = CreateEmailBlock(i, sizes[i]);
                await bm.WriteBlockAsync(block);
            }
        });
        
        var tinyOverhead = tinyResults - tinyEmails.Sum();
        _output.WriteLine($"  Overhead: {FormatBytes(tinyOverhead)} ({(double)tinyOverhead / tinyEmails.Sum() * 100:F1}%)");
        _output.WriteLine($"  Per-email overhead: {tinyOverhead / tinyEmails.Count} bytes");

        // Test 2: Few huge emails
        _output.WriteLine("\n📧 Case 2: Few huge emails (10MB each)");
        var hugeEmails = Enumerable.Range(0, 10).Select(i => 10 * 1024 * 1024).ToList();
        var hugeResults = await TestConfiguration("huge", hugeEmails, async (bm, sizes) =>
        {
            for (int i = 0; i < sizes.Count; i++)
            {
                var block = CreateEmailBlock(i, sizes[i]);
                await bm.WriteBlockAsync(block);
            }
        });
        
        var hugeOverhead = hugeResults - hugeEmails.Sum();
        _output.WriteLine($"  Overhead: {FormatBytes(hugeOverhead)} ({(double)hugeOverhead / hugeEmails.Sum() * 100:F1}%)");
        _output.WriteLine($"  Per-email overhead: {hugeOverhead / hugeEmails.Count} bytes");

        // Test 3: Bimodal distribution (mix of tiny and large)
        _output.WriteLine("\n📧 Case 3: Bimodal distribution");
        var bimodalEmails = new List<int>();
        // 80% tiny (500 bytes)
        bimodalEmails.AddRange(Enumerable.Range(0, 800).Select(i => 500));
        // 20% large (50KB)
        bimodalEmails.AddRange(Enumerable.Range(0, 200).Select(i => 50 * 1024));
        
        var bimodalResults = await TestConfiguration("bimodal", bimodalEmails, async (bm, sizes) =>
        {
            var hashChainManager = new HashChainManager(bm);
            for (int i = 0; i < sizes.Count; i++)
            {
                var block = CreateEmailBlock(i, sizes[i]);
                await bm.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
            }
        });
        
        var bimodalOverhead = bimodalResults - bimodalEmails.Sum();
        _output.WriteLine($"  Total emails: {bimodalEmails.Count}");
        _output.WriteLine($"  Total size: {FormatBytes(bimodalEmails.Sum())}");
        _output.WriteLine($"  With hash chain overhead: {FormatBytes(bimodalOverhead)} ({(double)bimodalOverhead / bimodalEmails.Sum() * 100:F1}%)");
    }

    private List<int> GenerateRealisticEmailSizes(int count, int avgSize, int stdDev)
    {
        var sizes = new List<int>();
        
        for (int i = 0; i < count; i++)
        {
            // Use normal distribution with bounds
            var size = (int)GetNormalDistributedValue(avgSize, stdDev);
            
            // Ensure minimum size (header + minimal content)
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
        // Box-Muller transform for normal distribution
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * randStdNormal;
    }

    private void PrintSizeDistribution(List<int> sizes)
    {
        var buckets = new[] { 1024, 5120, 10240, 25600, 51200, 102400, int.MaxValue };
        var bucketNames = new[] { "<1KB", "1-5KB", "5-10KB", "10-25KB", "25-50KB", "50-100KB", ">100KB" };
        var bucketCounts = new int[buckets.Length];
        
        foreach (var size in sizes)
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
                var percent = (double)bucketCounts[i] / sizes.Count * 100;
                _output.WriteLine($"  {bucketNames[i]}: {bucketCounts[i]} emails ({percent:F1}%)");
            }
        }
        
        _output.WriteLine($"  Min size: {FormatBytes(sizes.Min())}");
        _output.WriteLine($"  Max size: {FormatBytes(sizes.Max())}");
        _output.WriteLine($"  Median: {FormatBytes(sizes.OrderBy(s => s).ElementAt(sizes.Count / 2))}");
    }

    private void PrintDetailedDistribution(List<EmailInfo> emails)
    {
        var groups = emails.GroupBy(e => e.Category).OrderBy(g => g.Key);
        
        foreach (var group in groups)
        {
            var totalSize = group.Sum(e => (long)e.Size);
            var avgSize = totalSize / group.Count();
            _output.WriteLine($"  {group.Key}: {group.Count()} emails, avg {FormatBytes(avgSize)}, total {FormatBytes(totalSize)}");
        }
    }

    private List<EmailInfo> GenerateEmailsFromDistribution(int count, EmailDistribution distribution)
    {
        var emails = new List<EmailInfo>();
        var id = 0;
        
        foreach (var (range, percentage) in distribution)
        {
            var emailCount = (int)(count * percentage);
            var (minSize, maxSize) = range;
            
            for (int i = 0; i < emailCount; i++)
            {
                var size = _random.Next(minSize, maxSize);
                emails.Add(new EmailInfo
                {
                    Id = id++,
                    Size = size,
                    Category = GetSizeCategory(size)
                });
            }
        }
        
        return emails;
    }

    private string GetSizeCategory(int size)
    {
        return size switch
        {
            < 2000 => "Small",
            < 10000 => "Medium",
            < 100000 => "Large",
            _ => "Very Large"
        };
    }

    private void AnalyzeCompressionPotential(List<EmailInfo> emails)
    {
        // Estimate compression ratios based on email type
        var compressionRatios = new Dictionary<string, double>
        {
            { "Small", 0.7 },    // Text emails compress well
            { "Medium", 0.6 },   // Mixed content
            { "Large", 0.8 },    // Often contains attachments (already compressed)
            { "Very Large", 0.9 } // Mostly binary attachments
        };
        
        var groups = emails.GroupBy(e => e.Category);
        long originalTotal = 0;
        long compressedTotal = 0;
        
        foreach (var group in groups)
        {
            var groupSize = group.Sum(e => (long)e.Size);
            originalTotal += groupSize;
            compressedTotal += (long)(groupSize * compressionRatios[group.Key]);
            
            _output.WriteLine($"  {group.Key} emails: {(1 - compressionRatios[group.Key]) * 100:F0}% compression potential");
        }
        
        var overallSavings = originalTotal - compressedTotal;
        _output.WriteLine($"  Overall potential savings: {FormatBytes(overallSavings)} ({(double)overallSavings / originalTotal * 100:F1}%)");
    }

    private async Task<long> TestConfiguration(string name, List<int> emailSizes, Func<RawBlockManager, List<int>, Task> testAction)
    {
        var file = Path.Combine(_testDir, $"{name}_{Guid.NewGuid():N}.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            await testAction(blockManager, emailSizes);
        }
        
        return new FileInfo(file).Length;
    }

    private Block CreateEmailBlock(int id, int size)
    {
        // Create realistic email content
        var metadata = $"{{\"id\":{id},\"from\":\"sender{id}@example.com\",\"subject\":\"Email {id}\",\"date\":\"{DateTime.UtcNow:O}\"}}";
        var metadataBytes = Encoding.UTF8.GetBytes(metadata);
        
        var remainingSize = Math.Max(0, size - metadataBytes.Length);
        var content = new byte[remainingSize];
        
        // Fill with compressible data (simulating text)
        for (int i = 0; i < remainingSize; i++)
        {
            content[i] = (byte)('A' + (i % 26));
        }
        
        var payload = new byte[metadataBytes.Length + content.Length];
        metadataBytes.CopyTo(payload, 0);
        content.CopyTo(payload, metadataBytes.Length);
        
        return new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.Json,
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

    private class EmailInfo
    {
        public int Id { get; set; }
        public int Size { get; set; }
        public string Category { get; set; }
    }

    private class EmailDistribution : Dictionary<(int min, int max), double>
    {
    }
}