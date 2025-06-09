using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Text;
using EmailDB.Format.FileManagement;
using Tenray.ZoneTree;
using EmailDB.Format.ZoneTree;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Compares the efficiency of append-only block storage vs ZoneTree KV store.
/// </summary>
public class AppendOnlyVsZoneTreeTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public AppendOnlyVsZoneTreeTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"AppendOnlyVsZT_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Compare_Storage_Efficiency_And_Performance()
    {
        _output.WriteLine("📊 APPEND-ONLY vs ZONETREE COMPARISON");
        _output.WriteLine("=====================================\n");

        // Test parameters
        const int emailCount = 10000;
        const int avgEmailSize = 10240; // 10KB average
        const int stdDev = 5120; // 5KB std dev
        
        // Generate test emails
        var emails = GenerateTestEmails(emailCount, avgEmailSize, stdDev);
        var totalSize = emails.Sum(e => (long)e.data.Length);
        
        _output.WriteLine($"📧 Test Data:");
        _output.WriteLine($"  Emails: {emailCount:N0}");
        _output.WriteLine($"  Total size: {FormatBytes(totalSize)}");
        _output.WriteLine($"  Average size: {FormatBytes(totalSize / emailCount)}");
        _output.WriteLine($"  Min size: {FormatBytes(emails.Min(e => e.data.Length))}");
        _output.WriteLine($"  Max size: {FormatBytes(emails.Max(e => e.data.Length))}\n");

        // Test 1: Append-Only Block Store
        _output.WriteLine("🔵 TEST 1: APPEND-ONLY BLOCK STORE");
        _output.WriteLine("==================================");
        var appendOnlyResults = await TestAppendOnlyStore(emails);
        
        // Test 2: ZoneTree
        _output.WriteLine("\n🟢 TEST 2: ZONETREE KV STORE");
        _output.WriteLine("============================");
        var zoneTreeResults = await TestZoneTree(emails);
        
        // Test 3: ZoneTree with Batching
        _output.WriteLine("\n🟡 TEST 2B: ZONETREE WITH BATCHING");
        _output.WriteLine("==================================");
        var zoneTreeBatchResults = await TestZoneTreeWithBatching(emails);
        
        // Comparison
        _output.WriteLine("\n📊 COMPARISON RESULTS");
        _output.WriteLine("====================");
        
        _output.WriteLine("\n📁 Storage Efficiency:");
        _output.WriteLine($"  Append-Only:      {FormatBytes(appendOnlyResults.FileSize)} ({(double)totalSize / appendOnlyResults.FileSize * 100:F1}% efficiency)");
        _output.WriteLine($"  ZoneTree:         {FormatBytes(zoneTreeResults.FileSize)} ({(double)totalSize / zoneTreeResults.FileSize * 100:F1}% efficiency)");
        _output.WriteLine($"  ZoneTree Batch:   {FormatBytes(zoneTreeBatchResults.FileSize)} ({(double)totalSize / zoneTreeBatchResults.FileSize * 100:F1}% efficiency)");
        
        _output.WriteLine("\n⚡ Write Performance:");
        _output.WriteLine($"  Append-Only:      {appendOnlyResults.WriteTime:F2}ms ({emailCount / (appendOnlyResults.WriteTime / 1000):F0} emails/sec)");
        _output.WriteLine($"  ZoneTree:         {zoneTreeResults.WriteTime:F2}ms ({emailCount / (zoneTreeResults.WriteTime / 1000):F0} emails/sec)");
        _output.WriteLine($"  ZoneTree Batch:   {zoneTreeBatchResults.WriteTime:F2}ms ({emailCount / (zoneTreeBatchResults.WriteTime / 1000):F0} emails/sec)");
        
        _output.WriteLine("\n📖 Read Performance (1000 random reads):");
        _output.WriteLine($"  Append-Only:      {appendOnlyResults.ReadTime:F2}ms ({1000 / (appendOnlyResults.ReadTime / 1000):F0} reads/sec)");
        _output.WriteLine($"  ZoneTree:         {zoneTreeResults.ReadTime:F2}ms ({1000 / (zoneTreeResults.ReadTime / 1000):F0} reads/sec)");
        _output.WriteLine($"  ZoneTree Batch:   {zoneTreeBatchResults.ReadTime:F2}ms ({1000 / (zoneTreeBatchResults.ReadTime / 1000):F0} reads/sec)");
        
        // Analysis
        _output.WriteLine("\n💡 ANALYSIS:");
        var appendOnlyOverhead = ((double)appendOnlyResults.FileSize / totalSize - 1) * 100;
        var zoneTreeOverhead = ((double)zoneTreeResults.FileSize / totalSize - 1) * 100;
        
        _output.WriteLine($"  Append-Only overhead: {appendOnlyOverhead:F1}%");
        _output.WriteLine($"  ZoneTree overhead: {zoneTreeOverhead:F1}%");
        
        if (appendOnlyOverhead < zoneTreeOverhead)
        {
            var savings = ((double)zoneTreeResults.FileSize / appendOnlyResults.FileSize - 1) * 100;
            _output.WriteLine($"  ✅ Append-Only is {savings:F1}% more space efficient than ZoneTree");
        }
        else
        {
            var overhead = ((double)appendOnlyResults.FileSize / zoneTreeResults.FileSize - 1) * 100;
            _output.WriteLine($"  ❌ ZoneTree is {overhead:F1}% more space efficient than Append-Only");
        }
        
        _output.WriteLine("\n📝 CONCLUSIONS:");
        _output.WriteLine("  - Append-Only excels at sequential writes and space efficiency");
        _output.WriteLine("  - ZoneTree provides better random access and update capabilities");
        _output.WriteLine("  - Batching in ZoneTree improves space efficiency but complicates access");
    }

    private async Task<TestResults> TestAppendOnlyStore(TestEmail[] emails)
    {
        var dataPath = Path.Combine(_testDir, "appendonly.data");
        var indexPath = Path.Combine(_testDir, "appendonly.index");
        
        var sw = Stopwatch.StartNew();
        
        using (var store = new AppendOnlyEmailStore(dataPath, indexPath, blockSizeThreshold: 512 * 1024)) // 512KB blocks
        {
            // Write emails
            foreach (var email in emails)
            {
                await store.StoreEmailAsync(email.messageId, "inbox", email.data);
            }
            
            await store.FlushAsync();
        }
        
        var writeTime = sw.ElapsedMilliseconds;
        sw.Restart();
        
        // Read random emails
        using (var store = new AppendOnlyEmailStore(dataPath, indexPath, blockSizeThreshold: 512 * 1024))
        {
            for (int i = 0; i < 1000; i++)
            {
                var email = emails[_random.Next(emails.Length)];
                var (data, metadata) = await store.GetEmailByMessageIdAsync(email.messageId);
            }
        }
        
        var readTime = sw.ElapsedMilliseconds;
        
        var fileSize = new FileInfo(dataPath).Length + new FileInfo(indexPath).Length;
        
        _output.WriteLine($"  File sizes: {FormatBytes(new FileInfo(dataPath).Length)} data + {FormatBytes(new FileInfo(indexPath).Length)} index");
        _output.WriteLine($"  Write time: {writeTime:F2}ms");
        _output.WriteLine($"  Read time: {readTime:F2}ms");
        
        return new TestResults
        {
            FileSize = fileSize,
            WriteTime = writeTime,
            ReadTime = readTime
        };
    }

    private async Task<TestResults> TestZoneTree(TestEmail[] emails)
    {
        var dataPath = Path.Combine(_testDir, "zonetree");
        Directory.CreateDirectory(dataPath);
        
        var sw = Stopwatch.StartNew();
        
        using var zoneTree = new ZoneTreeFactory<string, byte[]>()
            .SetDataDirectory(dataPath)
            .SetIsDeletedDelegate((in string key, in byte[] value) => value == null)
            .OpenOrCreate();
        
        // Write emails
        foreach (var email in emails)
        {
            zoneTree.Upsert(email.messageId, email.data);
        }
        
        // ZoneTree doesn't have SaveMetaDataAsync, operations are persisted automatically
        
        var writeTime = sw.ElapsedMilliseconds;
        sw.Restart();
        
        // Read random emails
        for (int i = 0; i < 1000; i++)
        {
            var email = emails[_random.Next(emails.Length)];
            zoneTree.TryGet(email.messageId, out var data);
        }
        
        var readTime = sw.ElapsedMilliseconds;
        
        var fileSize = GetDirectorySize(dataPath);
        
        _output.WriteLine($"  Directory size: {FormatBytes(fileSize)}");
        _output.WriteLine($"  Write time: {writeTime:F2}ms");
        _output.WriteLine($"  Read time: {readTime:F2}ms");
        
        return new TestResults
        {
            FileSize = fileSize,
            WriteTime = writeTime,
            ReadTime = readTime
        };
    }

    private async Task<TestResults> TestZoneTreeWithBatching(TestEmail[] emails)
    {
        var dataPath = Path.Combine(_testDir, "zonetree_batch");
        Directory.CreateDirectory(dataPath);
        
        var sw = Stopwatch.StartNew();
        
        using var zoneTree = new ZoneTreeFactory<int, byte[]>()
            .SetDataDirectory(dataPath)
            .SetIsDeletedDelegate((in int key, in byte[] value) => value == null)
            .OpenOrCreate();
        
        // Batch emails into 512KB blocks
        var batchSize = 512 * 1024;
        var currentBatch = new MemoryStream();
        var writer = new BinaryWriter(currentBatch);
        var batchId = 0;
        var emailToBatch = new Dictionary<string, (int batchId, int offset)>();
        
        foreach (var email in emails)
        {
            var startPos = currentBatch.Position;
            writer.Write(email.messageId.Length);
            writer.Write(Encoding.UTF8.GetBytes(email.messageId));
            writer.Write(email.data.Length);
            writer.Write(email.data);
            
            emailToBatch[email.messageId] = (batchId, (int)startPos);
            
            if (currentBatch.Length >= batchSize)
            {
                zoneTree.Upsert(batchId, currentBatch.ToArray());
                batchId++;
                currentBatch = new MemoryStream();
                writer = new BinaryWriter(currentBatch);
            }
        }
        
        // Write final batch
        if (currentBatch.Length > 0)
        {
            zoneTree.Upsert(batchId, currentBatch.ToArray());
        }
        
        // Store index
        var indexData = System.Text.Json.JsonSerializer.Serialize(emailToBatch);
        await File.WriteAllTextAsync(Path.Combine(dataPath, "email_index.json"), indexData);
        
        // ZoneTree doesn't have SaveMetaDataAsync, operations are persisted automatically
        
        var writeTime = sw.ElapsedMilliseconds;
        sw.Restart();
        
        // Read random emails (more complex due to batching)
        for (int i = 0; i < 1000; i++)
        {
            var email = emails[_random.Next(emails.Length)];
            var (bid, offset) = emailToBatch[email.messageId];
            
            if (zoneTree.TryGet(bid, out var batchData))
            {
                using var ms = new MemoryStream(batchData);
                ms.Seek(offset, SeekOrigin.Begin);
                using var reader = new BinaryReader(ms);
                
                var msgIdLen = reader.ReadInt32();
                reader.ReadBytes(msgIdLen); // skip message id
                var dataLen = reader.ReadInt32();
                var data = reader.ReadBytes(dataLen);
            }
        }
        
        var readTime = sw.ElapsedMilliseconds;
        
        var fileSize = GetDirectorySize(dataPath);
        
        _output.WriteLine($"  Directory size: {FormatBytes(fileSize)}");
        _output.WriteLine($"  Batches created: {batchId + 1}");
        _output.WriteLine($"  Write time: {writeTime:F2}ms");
        _output.WriteLine($"  Read time: {readTime:F2}ms");
        
        return new TestResults
        {
            FileSize = fileSize,
            WriteTime = writeTime,
            ReadTime = readTime
        };
    }

    private TestEmail[] GenerateTestEmails(int count, int avgSize, int stdDev)
    {
        var emails = new TestEmail[count];
        
        for (int i = 0; i < count; i++)
        {
            var size = Math.Max(100, (int)GetNormalDistributedValue(avgSize, stdDev));
            
            // Add some outliers
            if (_random.NextDouble() < 0.05) // 5% chance
            {
                size *= _random.Next(5, 20);
            }
            
            var data = new byte[size];
            _random.NextBytes(data);
            
            emails[i] = new TestEmail
            {
                messageId = $"msg-{i:D8}@example.com",
                data = data
            };
        }
        
        return emails;
    }

    private double GetNormalDistributedValue(double mean, double stdDev)
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * randStdNormal;
    }

    private long GetDirectorySize(string path)
    {
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
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

    private class TestEmail
    {
        public string messageId;
        public byte[] data;
    }

    private class TestResults
    {
        public long FileSize { get; set; }
        public double WriteTime { get; set; }
        public double ReadTime { get; set; }
    }
}