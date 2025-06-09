using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Endurance tests with large datasets to verify system stability and performance at scale.
/// </summary>
public class LargeDatasetEnduranceTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public LargeDatasetEnduranceTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"EnduranceTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Theory]
    [InlineData(10000, 256 * 1024)]   // 10K emails, 256KB blocks
    [InlineData(50000, 512 * 1024)]   // 50K emails, 512KB blocks
    [InlineData(100000, 1024 * 1024)] // 100K emails, 1MB blocks
    public async Task Test_Large_Dataset_Performance_Over_Time(int emailCount, int blockSize)
    {
        _output.WriteLine($"📊 LARGE DATASET ENDURANCE TEST");
        _output.WriteLine($"===============================");
        _output.WriteLine($"  Target: {emailCount:N0} emails");
        _output.WriteLine($"  Block size: {FormatBytes(blockSize)}");
        
        var dataPath = Path.Combine(_testDir, $"endurance_{emailCount}.data");
        var indexPath = Path.Combine(_testDir, $"indexes_{emailCount}");
        
        using var store = new HybridEmailStore(dataPath, indexPath, blockSize);
        
        var performanceMetrics = new List<PerformanceMetric>();
        var batchSize = 1000;
        var totalBatches = emailCount / batchSize;
        
        // Track initial memory
        var initialMemory = GC.GetTotalMemory(true);
        _output.WriteLine($"  Initial memory: {FormatBytes(initialMemory)}");
        
        // Write emails in batches and track performance
        _output.WriteLine($"\n📝 WRITE PHASE");
        _output.WriteLine($"=============");
        
        var totalWriteTime = 0L;
        var totalSize = 0L;
        var emailIds = new List<EmailId>();
        
        for (int batch = 0; batch < totalBatches; batch++)
        {
            var batchSw = Stopwatch.StartNew();
            var batchSize2 = 0L;
            
            for (int i = 0; i < batchSize; i++)
            {
                var emailIndex = batch * batchSize + i;
                var emailData = GenerateEmail(emailIndex);
                batchSize2 += emailData.data.Length;
                totalSize += emailData.data.Length;
                
                var emailId = await store.StoreEmailAsync(
                    emailData.messageId,
                    emailData.folder,
                    emailData.data,
                    emailData.subject,
                    emailData.from,
                    emailData.to,
                    emailData.body,
                    emailData.date
                );
                
                emailIds.Add(emailId);
            }
            
            batchSw.Stop();
            totalWriteTime += batchSw.ElapsedMilliseconds;
            
            // Track metrics every 10 batches
            if (batch % 10 == 0 || batch == totalBatches - 1)
            {
                await store.FlushAsync();
                var currentMemory = GC.GetTotalMemory(false);
                var stats = store.GetStats();
                
                var metric = new PerformanceMetric
                {
                    BatchNumber = batch,
                    EmailsWritten = (batch + 1) * batchSize,
                    WriteTimeMs = batchSw.ElapsedMilliseconds,
                    TotalSizeMB = totalSize / (1024.0 * 1024.0),
                    FileSystemSizeMB = stats.TotalSize / (1024.0 * 1024.0),
                    MemoryUsageMB = (currentMemory - initialMemory) / (1024.0 * 1024.0),
                    WriteThroughputMBps = (batchSize2 / 1024.0 / 1024.0) / (batchSw.ElapsedMilliseconds / 1000.0)
                };
                
                performanceMetrics.Add(metric);
                
                if (batch % 50 == 0)
                {
                    _output.WriteLine($"  Batch {batch}/{totalBatches}: {metric.WriteThroughputMBps:F2} MB/s, " +
                                    $"Memory: {metric.MemoryUsageMB:F1} MB");
                }
            }
            
            // Simulate real-world delays
            if (batch % 100 == 0)
            {
                await Task.Delay(10);
            }
        }
        
        await store.FlushAsync();
        
        _output.WriteLine($"\n  Write complete:");
        _output.WriteLine($"    Total time: {totalWriteTime / 1000.0:F1}s");
        _output.WriteLine($"    Average throughput: {totalSize / 1024.0 / 1024.0 / (totalWriteTime / 1000.0):F2} MB/s");
        
        // Read performance test
        _output.WriteLine($"\n📖 READ PHASE");
        _output.WriteLine($"============");
        
        var readBatches = Math.Min(10, totalBatches);
        var readsPerBatch = 100;
        var totalReadTime = 0L;
        
        for (int batch = 0; batch < readBatches; batch++)
        {
            var batchSw = Stopwatch.StartNew();
            
            for (int i = 0; i < readsPerBatch; i++)
            {
                var randomIndex = _random.Next(emailIds.Count);
                var (data, metadata) = await store.GetEmailAsync(emailIds[randomIndex]);
                
                Assert.NotNull(data);
                Assert.NotNull(metadata);
            }
            
            batchSw.Stop();
            totalReadTime += batchSw.ElapsedMilliseconds;
        }
        
        var avgReadLatency = totalReadTime / (double)(readBatches * readsPerBatch);
        _output.WriteLine($"  Average read latency: {avgReadLatency:F2}ms");
        _output.WriteLine($"  Reads/second: {1000.0 / avgReadLatency:F0}");
        
        // Search performance test
        _output.WriteLine($"\n🔍 SEARCH PHASE");
        _output.WriteLine($"==============");
        
        var searchWords = new[] { "important", "meeting", "project", "email", "data" };
        var searchTimes = new List<long>();
        
        foreach (var word in searchWords)
        {
            var sw = Stopwatch.StartNew();
            var results = store.SearchFullText(word).Take(100).ToList();
            sw.Stop();
            
            searchTimes.Add(sw.ElapsedMilliseconds);
            _output.WriteLine($"  Search '{word}': {results.Count} results in {sw.ElapsedMilliseconds}ms");
        }
        
        // Folder operations
        _output.WriteLine($"\n📂 FOLDER OPERATIONS");
        _output.WriteLine($"===================");
        
        var folders = new[] { "folder-0", "folder-1", "folder-2" };
        foreach (var folder in folders)
        {
            var sw = Stopwatch.StartNew();
            var count = store.ListFolder(folder).Count();
            sw.Stop();
            
            _output.WriteLine($"  {folder}: {count:N0} emails in {sw.ElapsedMilliseconds}ms");
        }
        
        // Storage efficiency
        _output.WriteLine($"\n💾 STORAGE ANALYSIS");
        _output.WriteLine($"==================");
        
        var finalStats = store.GetStats();
        var efficiency = (totalSize * 100.0) / finalStats.TotalSize;
        
        _output.WriteLine($"  Email data: {FormatBytes(totalSize)}");
        _output.WriteLine($"  Data file: {FormatBytes(finalStats.DataFileSize)}");
        _output.WriteLine($"  Index size: {FormatBytes(finalStats.IndexSize)}");
        _output.WriteLine($"  Total size: {FormatBytes(finalStats.TotalSize)}");
        _output.WriteLine($"  Efficiency: {efficiency:F1}%");
        _output.WriteLine($"  Overhead: {100 - efficiency:F1}%");
        
        // Performance degradation analysis
        _output.WriteLine($"\n📉 PERFORMANCE DEGRADATION ANALYSIS");
        _output.WriteLine($"==================================");
        
        if (performanceMetrics.Count > 2)
        {
            var firstQuarter = performanceMetrics.Take(performanceMetrics.Count / 4).Average(m => m.WriteThroughputMBps);
            var lastQuarter = performanceMetrics.Skip(3 * performanceMetrics.Count / 4).Average(m => m.WriteThroughputMBps);
            var degradation = (firstQuarter - lastQuarter) / firstQuarter * 100;
            
            _output.WriteLine($"  First quarter avg: {firstQuarter:F2} MB/s");
            _output.WriteLine($"  Last quarter avg: {lastQuarter:F2} MB/s");
            _output.WriteLine($"  Degradation: {degradation:F1}%");
            
            Assert.True(degradation < 20, $"Performance degradation too high: {degradation:F1}%");
        }
        
        // Memory growth analysis
        var finalMemory = GC.GetTotalMemory(true);
        var memoryGrowth = (finalMemory - initialMemory) / (1024.0 * 1024.0);
        var memoryPerEmail = memoryGrowth / emailCount * 1000; // KB per 1000 emails
        
        _output.WriteLine($"\n🧠 MEMORY ANALYSIS");
        _output.WriteLine($"=================");
        _output.WriteLine($"  Total growth: {memoryGrowth:F1} MB");
        _output.WriteLine($"  Per 1000 emails: {memoryPerEmail:F1} KB");
        
        // Assertions
        Assert.True(efficiency > 80, $"Storage efficiency too low: {efficiency:F1}%");
        Assert.True(memoryPerEmail < 100, $"Memory usage too high: {memoryPerEmail:F1} KB per 1000 emails");
    }

    [Fact]
    public async Task Test_Sustained_Load_Pattern()
    {
        _output.WriteLine("\n⏱️ SUSTAINED LOAD TEST");
        _output.WriteLine("=====================");
        
        var dataPath = Path.Combine(_testDir, "sustained_load.data");
        var indexPath = Path.Combine(_testDir, "sustained_indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath, 512 * 1024);
        
        var duration = TimeSpan.FromSeconds(30); // Run for 30 seconds
        var sw = Stopwatch.StartNew();
        var operations = new List<(string type, long elapsed)>();
        
        var emailCount = 0;
        var readCount = 0;
        var searchCount = 0;
        var errorCount = 0;
        
        _output.WriteLine($"  Running for {duration.TotalSeconds} seconds...");
        
        // Sustained mixed operations
        while (sw.Elapsed < duration)
        {
            var op = _random.Next(100);
            
            try
            {
                if (op < 60) // 60% writes
                {
                    var email = GenerateEmail(emailCount++);
                    var opSw = Stopwatch.StartNew();
                    
                    await store.StoreEmailAsync(
                        email.messageId, email.folder, email.data,
                        email.subject, email.from, email.to, email.body, email.date
                    );
                    
                    operations.Add(("write", opSw.ElapsedMilliseconds));
                }
                else if (op < 85 && emailCount > 0) // 25% reads
                {
                    var opSw = Stopwatch.StartNew();
                    var messageId = $"sustained-{_random.Next(emailCount)}@test.com";
                    
                    try
                    {
                        var (data, meta) = await store.GetEmailByMessageIdAsync(messageId);
                        readCount++;
                    }
                    catch (KeyNotFoundException) { }
                    
                    operations.Add(("read", opSw.ElapsedMilliseconds));
                }
                else // 15% searches
                {
                    var opSw = Stopwatch.StartNew();
                    var results = store.SearchFullText("email").Take(10).ToList();
                    searchCount++;
                    
                    operations.Add(("search", opSw.ElapsedMilliseconds));
                }
            }
            catch (Exception)
            {
                errorCount++;
            }
            
            // Prevent tight loop
            if (operations.Count % 100 == 0)
            {
                await Task.Delay(1);
            }
        }
        
        sw.Stop();
        
        // Analyze operations
        var writeOps = operations.Where(o => o.type == "write").ToList();
        var readOps = operations.Where(o => o.type == "read").ToList();
        var searchOps = operations.Where(o => o.type == "search").ToList();
        
        _output.WriteLine($"\n  Results:");
        _output.WriteLine($"    Total operations: {operations.Count:N0}");
        _output.WriteLine($"    Writes: {writeOps.Count:N0} (avg {writeOps.Average(o => o.elapsed):F1}ms)");
        _output.WriteLine($"    Reads: {readOps.Count:N0} (avg {readOps.Average(o => o.elapsed):F1}ms)");
        _output.WriteLine($"    Searches: {searchOps.Count:N0} (avg {searchOps.Average(o => o.elapsed):F1}ms)");
        _output.WriteLine($"    Errors: {errorCount}");
        _output.WriteLine($"    Ops/second: {operations.Count / sw.Elapsed.TotalSeconds:F0}");
        
        Assert.True(errorCount < operations.Count * 0.01, "Error rate too high");
        Assert.True(operations.Count > 100, "Not enough operations completed");
    }

    private (string messageId, string folder, byte[] data, string subject, string from, string to, string body, DateTime date) 
        GenerateEmail(int index)
    {
        var size = GetRealisticEmailSize();
        var folder = $"folder-{index % 10}";
        var messageId = $"sustained-{index}@test.com";
        var subject = $"Email {index}: {GetRandomSubject()}";
        var from = $"sender{index % 100}@example.com";
        var to = $"recipient{(index + 1) % 100}@example.com";
        var body = GenerateEmailBody(size, index);
        var date = DateTime.UtcNow.AddDays(-_random.Next(365));
        
        return (messageId, folder, Encoding.UTF8.GetBytes(body), subject, from, to, body, date);
    }

    private string GenerateEmailBody(int targetSize, int index)
    {
        var keywords = new[] { "important", "meeting", "project", "update", "review", "proposal", "email", "data" };
        var sb = new StringBuilder();
        
        sb.AppendLine($"This is email {index}.");
        sb.AppendLine($"Keywords: {string.Join(", ", keywords.OrderBy(_ => Guid.NewGuid()).Take(3))}");
        sb.AppendLine();
        
        while (sb.Length < targetSize)
        {
            sb.AppendLine("Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
                         "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.");
        }
        
        return sb.ToString().Substring(0, targetSize);
    }

    private int GetRealisticEmailSize()
    {
        var rand = _random.NextDouble();
        
        if (rand < 0.4) // 40% small emails
            return _random.Next(500, 2000);
        else if (rand < 0.75) // 35% medium emails  
            return _random.Next(2000, 10000);
        else if (rand < 0.95) // 20% large emails
            return _random.Next(10000, 50000);
        else // 5% very large emails
            return _random.Next(50000, 200000);
    }

    private string GetRandomSubject()
    {
        var subjects = new[]
        {
            "Important Update",
            "Meeting Request",
            "Project Status",
            "Weekly Report",
            "Action Required",
            "FYI",
            "Question about project",
            "Schedule Change",
            "Document Review",
            "Follow-up"
        };
        
        return subjects[_random.Next(subjects.Length)];
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
        catch { }
    }

    private class PerformanceMetric
    {
        public int BatchNumber { get; set; }
        public int EmailsWritten { get; set; }
        public long WriteTimeMs { get; set; }
        public double TotalSizeMB { get; set; }
        public double FileSystemSizeMB { get; set; }
        public double MemoryUsageMB { get; set; }
        public double WriteThroughputMBps { get; set; }
    }
}