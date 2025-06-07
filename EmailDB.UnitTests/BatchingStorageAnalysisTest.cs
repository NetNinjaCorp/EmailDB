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
/// Analyzes storage efficiency with email batching strategies.
/// </summary>
public class BatchingStorageAnalysisTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public BatchingStorageAnalysisTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"BatchingAnalysis_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Theory]
    [InlineData(256 * 1024)]    // 256KB batches
    [InlineData(512 * 1024)]    // 512KB batches  
    [InlineData(1024 * 1024)]   // 1MB batches
    [InlineData(2048 * 1024)]   // 2MB batches
    [InlineData(5120 * 1024)]   // 5MB batches
    public async Task Analyze_Batching_Efficiency(int batchSizeThreshold)
    {
        _output.WriteLine($"📦 BATCHING ANALYSIS - {FormatBytes(batchSizeThreshold)} batches");
        _output.WriteLine($"==================================================");

        // Generate realistic email distribution
        var emails = GenerateRealisticEmails(1000);
        var totalRawSize = emails.Sum(e => (long)e.Size);
        
        _output.WriteLine($"📊 Test Data:");
        _output.WriteLine($"  Total emails: {emails.Count}");
        _output.WriteLine($"  Total raw size: {FormatBytes(totalRawSize)}");
        _output.WriteLine($"  Average email: {FormatBytes(totalRawSize / emails.Count)}");

        // Test 1: No batching (baseline)
        var noBatchingSize = await TestNoBatching(emails);
        
        // Test 2: Simple batching
        var simpleBatchingSize = await TestSimpleBatching(emails, batchSizeThreshold);
        
        // Test 3: Smart batching (with metadata preservation)
        var smartBatchingSize = await TestSmartBatching(emails, batchSizeThreshold);
        
        // Test 4: Compressed batching
        var compressedBatchingSize = await TestCompressedBatching(emails, batchSizeThreshold);

        // Results
        _output.WriteLine($"\n📈 RESULTS:");
        _output.WriteLine($"  No batching:         {FormatBytes(noBatchingSize)} (baseline)");
        _output.WriteLine($"  Simple batching:     {FormatBytes(simpleBatchingSize)} ({GetSavingsPercent(simpleBatchingSize, noBatchingSize):F1}% savings)");
        _output.WriteLine($"  Smart batching:      {FormatBytes(smartBatchingSize)} ({GetSavingsPercent(smartBatchingSize, noBatchingSize):F1}% savings)");
        _output.WriteLine($"  Compressed batching: {FormatBytes(compressedBatchingSize)} ({GetSavingsPercent(compressedBatchingSize, noBatchingSize):F1}% savings)");
        
        // Analysis
        var optimalBatch = simpleBatchingSize < smartBatchingSize ? "Simple" : "Smart";
        _output.WriteLine($"\n💡 INSIGHTS:");
        _output.WriteLine($"  Batching saves {GetSavingsPercent(simpleBatchingSize, noBatchingSize):F1}% on average");
        _output.WriteLine($"  {optimalBatch} batching is more efficient for this batch size");
        _output.WriteLine($"  Compression adds {GetSavingsPercent(compressedBatchingSize, simpleBatchingSize):F1}% additional savings");
    }

    [Fact]
    public async Task Analyze_Optimal_Batch_Size()
    {
        _output.WriteLine("🎯 OPTIMAL BATCH SIZE ANALYSIS");
        _output.WriteLine("=============================");

        var emails = GenerateRealisticEmails(5000);
        var totalRawSize = emails.Sum(e => (long)e.Size);
        
        _output.WriteLine($"📊 Test Data: {emails.Count} emails, {FormatBytes(totalRawSize)} total");

        var batchSizes = new[] { 128, 256, 512, 1024, 2048, 4096, 8192 }.Select(kb => kb * 1024).ToArray();
        var results = new Dictionary<int, BatchingResult>();

        foreach (var batchSize in batchSizes)
        {
            results[batchSize] = await AnalyzeBatchSize(emails, batchSize);
        }

        // Find optimal size
        var optimal = results.OrderBy(r => r.Value.FileSize).First();
        
        _output.WriteLine($"\n📊 BATCH SIZE COMPARISON:");
        foreach (var (size, result) in results.OrderBy(r => r.Key))
        {
            var savings = GetSavingsPercent(result.FileSize, results[batchSizes[0]].FileSize);
            var marker = size == optimal.Key ? " ⭐ OPTIMAL" : "";
            _output.WriteLine($"  {FormatBytes(size),10}: {FormatBytes(result.FileSize),10} | " +
                            $"{result.BatchCount,4} batches | " +
                            $"{result.AverageFillRate:F1}% fill rate | " +
                            $"{savings:F1}% savings{marker}");
        }

        _output.WriteLine($"\n💡 RECOMMENDATIONS:");
        _output.WriteLine($"  Optimal batch size: {FormatBytes(optimal.Key)}");
        _output.WriteLine($"  Results in {optimal.Value.BatchCount} batches with {optimal.Value.AverageFillRate:F1}% fill rate");
        
        if (optimal.Key >= 1024 * 1024)
        {
            _output.WriteLine($"  Consider memory usage - batches are {FormatBytes(optimal.Key)} each");
        }
    }

    [Fact]
    public async Task Analyze_Batching_With_Updates()
    {
        _output.WriteLine("🔄 BATCHING WITH UPDATES ANALYSIS");
        _output.WriteLine("================================");

        var emails = GenerateRealisticEmails(1000);
        const int batchSize = 512 * 1024; // 512KB batches
        
        // Simulate different update patterns
        var updateRates = new[] { 0.05, 0.15, 0.30, 0.50 };
        
        foreach (var updateRate in updateRates)
        {
            _output.WriteLine($"\n📊 Update rate: {updateRate:P0}");
            
            var file = Path.Combine(_testDir, $"batch_updates_{updateRate}.emdb");
            var batch = new EmailBatch(batchSize);
            var writtenBatches = 0;
            var totalSize = 0L;
            var affectedBatches = 0;
            
            using (var blockManager = new RawBlockManager(file))
            {
                // Initial write with batching
                foreach (var email in emails)
                {
                    if (!batch.TryAdd(email))
                    {
                        // Write batch
                        await WriteBatch(blockManager, batch, writtenBatches++);
                        batch = new EmailBatch(batchSize);
                        batch.TryAdd(email);
                    }
                }
                
                // Write final batch
                if (batch.Count > 0)
                {
                    await WriteBatch(blockManager, batch, writtenBatches++);
                }
                
                // Simulate updates
                var emailsToUpdate = emails
                    .OrderBy(e => _random.Next())
                    .Take((int)(emails.Count * updateRate))
                    .ToList();
                
                // In batched system, updates might require rewriting entire batches
                affectedBatches = emailsToUpdate
                    .Select(e => e.Id / (batchSize / 2048)) // Estimate batch assignment
                    .Distinct()
                    .Count();
                
                _output.WriteLine($"  Emails updated: {emailsToUpdate.Count}");
                _output.WriteLine($"  Batches affected: {affectedBatches} of {writtenBatches}");
                
                // Simulate batch rewrites
                foreach (var batchId in Enumerable.Range(0, affectedBatches))
                {
                    var updateBatch = new EmailBatch(batchSize);
                    // Add placeholder data
                    for (int i = 0; i < 10; i++)
                    {
                        updateBatch.TryAdd(new EmailInfo { Id = 10000 + batchId * 10 + i, Size = 5000 });
                    }
                    await WriteBatch(blockManager, updateBatch, writtenBatches++);
                }
            }
            
            var fileInfo = new FileInfo(file);
            var efficiency = (double)emails.Sum(e => (long)e.Size) / fileInfo.Length * 100;
            var rewriteOverhead = (long)affectedBatches * batchSize;
            
            _output.WriteLine($"  File size: {FormatBytes(fileInfo.Length)}");
            _output.WriteLine($"  Space efficiency: {efficiency:F1}%");
            totalSize = fileInfo.Length;
            _output.WriteLine($"  Batch rewrite overhead: {FormatBytes(rewriteOverhead)}");
        }

        _output.WriteLine($"\n💡 INSIGHTS:");
        _output.WriteLine($"  Batching reduces efficiency with frequent updates");
        _output.WriteLine($"  Consider hybrid approach: batch old emails, individual storage for recent");
    }

    [Fact]
    public async Task Compare_Batching_Strategies()
    {
        _output.WriteLine("🔀 BATCHING STRATEGIES COMPARISON");
        _output.WriteLine("================================");

        var emails = GenerateRealisticEmails(2000);
        const int batchSize = 1024 * 1024; // 1MB
        
        // Strategy 1: Time-based batching
        var timeBasedSize = await TestTimeBasedBatching(emails, TimeSpan.FromMinutes(5));
        
        // Strategy 2: Size-based batching
        var sizeBasedSize = await TestSimpleBatching(emails, batchSize);
        
        // Strategy 3: Hybrid (size + time)
        var hybridSize = await TestHybridBatching(emails, batchSize, TimeSpan.FromMinutes(5));
        
        // Strategy 4: Adaptive batching (varies batch size based on email patterns)
        var adaptiveSize = await TestAdaptiveBatching(emails);

        _output.WriteLine($"\n📊 RESULTS:");
        _output.WriteLine($"  Time-based:     {FormatBytes(timeBasedSize)}");
        _output.WriteLine($"  Size-based:     {FormatBytes(sizeBasedSize)}");
        _output.WriteLine($"  Hybrid:         {FormatBytes(hybridSize)}");
        _output.WriteLine($"  Adaptive:       {FormatBytes(adaptiveSize)}");
        
        _output.WriteLine($"\n💡 RECOMMENDATIONS:");
        var best = Math.Min(Math.Min(timeBasedSize, sizeBasedSize), Math.Min(hybridSize, adaptiveSize));
        if (best == adaptiveSize)
        {
            _output.WriteLine($"  ✅ Adaptive batching provides best efficiency");
            _output.WriteLine($"     Adjusts batch size based on email patterns");
        }
        else if (best == hybridSize)
        {
            _output.WriteLine($"  ✅ Hybrid approach balances efficiency and latency");
            _output.WriteLine($"     Good for real-time systems");
        }
    }

    private async Task<long> TestNoBatching(List<EmailInfo> emails)
    {
        var file = Path.Combine(_testDir, $"nobatch_{Guid.NewGuid():N}.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            foreach (var email in emails)
            {
                var block = CreateEmailBlock(email.Id, email.Size);
                await blockManager.WriteBlockAsync(block);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestSimpleBatching(List<EmailInfo> emails, int batchSizeThreshold)
    {
        var file = Path.Combine(_testDir, $"simplebatch_{Guid.NewGuid():N}.emdb");
        var batch = new EmailBatch(batchSizeThreshold);
        var batchId = 0;
        
        using (var blockManager = new RawBlockManager(file))
        {
            foreach (var email in emails)
            {
                if (!batch.TryAdd(email))
                {
                    // Write current batch
                    await WriteBatch(blockManager, batch, batchId++);
                    
                    // Start new batch
                    batch = new EmailBatch(batchSizeThreshold);
                    batch.TryAdd(email);
                }
            }
            
            // Write final batch if not empty
            if (batch.Count > 0)
            {
                await WriteBatch(blockManager, batch, batchId);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestSmartBatching(List<EmailInfo> emails, int batchSizeThreshold)
    {
        var file = Path.Combine(_testDir, $"smartbatch_{Guid.NewGuid():N}.emdb");
        var batch = new SmartEmailBatch(batchSizeThreshold);
        var batchId = 0;
        
        using (var blockManager = new RawBlockManager(file))
        {
            var hashChainManager = new HashChainManager(blockManager);
            
            foreach (var email in emails)
            {
                if (!batch.TryAdd(email))
                {
                    // Write current batch with metadata
                    var batchBlock = await WriteBatchWithMetadata(blockManager, batch, batchId++);
                    await hashChainManager.AddToChainAsync(batchBlock);
                    
                    // Start new batch
                    batch = new SmartEmailBatch(batchSizeThreshold);
                    batch.TryAdd(email);
                }
            }
            
            // Write final batch
            if (batch.Count > 0)
            {
                var batchBlock = await WriteBatchWithMetadata(blockManager, batch, batchId);
                await hashChainManager.AddToChainAsync(batchBlock);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestCompressedBatching(List<EmailInfo> emails, int batchSizeThreshold)
    {
        var file = Path.Combine(_testDir, $"compressedbatch_{Guid.NewGuid():N}.emdb");
        var batch = new EmailBatch(batchSizeThreshold);
        var batchId = 0;
        
        using (var blockManager = new RawBlockManager(file))
        {
            foreach (var email in emails)
            {
                if (!batch.TryAdd(email))
                {
                    await WriteCompressedBatch(blockManager, batch, batchId++);
                    batch = new EmailBatch(batchSizeThreshold);
                    batch.TryAdd(email);
                }
            }
            
            if (batch.Count > 0)
            {
                await WriteCompressedBatch(blockManager, batch, batchId);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestTimeBasedBatching(List<EmailInfo> emails, TimeSpan batchWindow)
    {
        var file = Path.Combine(_testDir, $"timebatch_{Guid.NewGuid():N}.emdb");
        var batches = new List<EmailBatch>();
        var currentBatch = new EmailBatch(int.MaxValue); // No size limit
        var currentTime = DateTime.UtcNow;
        
        // Simulate emails arriving over time
        foreach (var email in emails)
        {
            email.ArrivalTime = currentTime;
            currentBatch.Add(email);
            
            // Simulate time passing
            currentTime = currentTime.AddSeconds(_random.Next(1, 30));
            
            // Check if batch window expired
            if (currentTime - currentBatch.StartTime > batchWindow)
            {
                batches.Add(currentBatch);
                currentBatch = new EmailBatch(int.MaxValue) { StartTime = currentTime };
            }
        }
        
        if (currentBatch.Count > 0)
        {
            batches.Add(currentBatch);
        }
        
        // Write batches
        using (var blockManager = new RawBlockManager(file))
        {
            for (int i = 0; i < batches.Count; i++)
            {
                await WriteBatch(blockManager, batches[i], i);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestHybridBatching(List<EmailInfo> emails, int sizeThreshold, TimeSpan timeThreshold)
    {
        var file = Path.Combine(_testDir, $"hybridbatch_{Guid.NewGuid():N}.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            var batch = new EmailBatch(sizeThreshold);
            var batchId = 0;
            var lastWriteTime = DateTime.UtcNow;
            
            foreach (var email in emails)
            {
                var currentTime = DateTime.UtcNow;
                
                // Check time threshold
                if (currentTime - lastWriteTime > timeThreshold && batch.Count > 0)
                {
                    await WriteBatch(blockManager, batch, batchId++);
                    batch = new EmailBatch(sizeThreshold);
                    lastWriteTime = currentTime;
                }
                
                // Try to add email
                if (!batch.TryAdd(email))
                {
                    await WriteBatch(blockManager, batch, batchId++);
                    batch = new EmailBatch(sizeThreshold);
                    batch.TryAdd(email);
                    lastWriteTime = currentTime;
                }
            }
            
            if (batch.Count > 0)
            {
                await WriteBatch(blockManager, batch, batchId);
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task<long> TestAdaptiveBatching(List<EmailInfo> emails)
    {
        var file = Path.Combine(_testDir, $"adaptivebatch_{Guid.NewGuid():N}.emdb");
        
        using (var blockManager = new RawBlockManager(file))
        {
            var batchId = 0;
            var i = 0;
            
            while (i < emails.Count)
            {
                // Analyze next set of emails to determine optimal batch size
                var lookahead = emails.Skip(i).Take(50).ToList();
                var avgSize = lookahead.Count > 0 ? lookahead.Average(e => e.Size) : 5000;
                
                // Adapt batch size based on email characteristics
                int batchSize;
                if (avgSize < 2000) // Small emails
                {
                    batchSize = 2 * 1024 * 1024; // 2MB batches
                }
                else if (avgSize < 10000) // Medium emails
                {
                    batchSize = 1024 * 1024; // 1MB batches
                }
                else // Large emails
                {
                    batchSize = 512 * 1024; // 512KB batches
                }
                
                var batch = new EmailBatch(batchSize);
                
                while (i < emails.Count && batch.TryAdd(emails[i]))
                {
                    i++;
                }
                
                if (batch.Count > 0)
                {
                    await WriteBatch(blockManager, batch, batchId++);
                }
            }
        }
        
        return new FileInfo(file).Length;
    }

    private async Task WriteBatch(RawBlockManager blockManager, EmailBatch batch, int batchId)
    {
        var batchData = batch.Serialize();
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0x20, // Batch flag
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = batchId,
            Payload = batchData
        };
        
        await blockManager.WriteBlockAsync(block);
    }

    private async Task<Block> WriteBatchWithMetadata(RawBlockManager blockManager, SmartEmailBatch batch, int batchId)
    {
        var batchData = batch.SerializeWithMetadata();
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0x21, // Smart batch flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = batchId,
            Payload = batchData
        };
        
        await blockManager.WriteBlockAsync(block);
        return block;
    }

    private async Task WriteCompressedBatch(RawBlockManager blockManager, EmailBatch batch, int batchId)
    {
        var batchData = batch.Serialize();
        var compressedData = CompressData(batchData);
        
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0x22, // Compressed batch flag
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = batchId,
            Payload = compressedData
        };
        
        await blockManager.WriteBlockAsync(block);
    }

    private byte[] CompressData(byte[] data)
    {
        // Simple simulation - in reality would use proper compression
        // Assume 60% compression ratio for email text
        var compressedSize = (int)(data.Length * 0.6);
        var compressed = new byte[compressedSize];
        Array.Copy(data, compressed, Math.Min(data.Length, compressedSize));
        return compressed;
    }

    private async Task<BatchingResult> AnalyzeBatchSize(List<EmailInfo> emails, int batchSize)
    {
        var batches = 0;
        var totalFillRate = 0.0;
        var batch = new EmailBatch(batchSize);
        
        foreach (var email in emails)
        {
            if (!batch.TryAdd(email))
            {
                batches++;
                totalFillRate += batch.FillRate;
                batch = new EmailBatch(batchSize);
                batch.TryAdd(email);
            }
        }
        
        if (batch.Count > 0)
        {
            batches++;
            totalFillRate += batch.FillRate;
        }
        
        // Calculate file size
        var file = Path.Combine(_testDir, $"analyze_{batchSize}.emdb");
        using (var blockManager = new RawBlockManager(file))
        {
            for (int i = 0; i < batches; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0x20,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = i,
                    Payload = new byte[batchSize / 2] // Average fill
                };
                await blockManager.WriteBlockAsync(block);
            }
        }
        
        return new BatchingResult
        {
            BatchCount = batches,
            FileSize = new FileInfo(file).Length,
            AverageFillRate = totalFillRate / batches * 100
        };
    }

    private List<EmailInfo> GenerateRealisticEmails(int count)
    {
        var emails = new List<EmailInfo>();
        
        for (int i = 0; i < count; i++)
        {
            int size;
            var rand = _random.NextDouble();
            
            if (rand < 0.4) // 40% small
            {
                size = _random.Next(100, 2000);
            }
            else if (rand < 0.75) // 35% medium
            {
                size = _random.Next(2000, 10000);
            }
            else if (rand < 0.95) // 20% large
            {
                size = _random.Next(10000, 50000);
            }
            else // 5% very large
            {
                size = _random.Next(50000, 200000);
            }
            
            emails.Add(new EmailInfo { Id = i, Size = size });
        }
        
        return emails;
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

    private double GetSavingsPercent(long newSize, long originalSize)
    {
        return (1.0 - (double)newSize / originalSize) * 100;
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
        public DateTime ArrivalTime { get; set; }
    }

    private class EmailBatch
    {
        protected readonly List<EmailInfo> _emails = new();
        private readonly int _maxSize;
        protected int _currentSize;

        public EmailBatch(int maxSize)
        {
            _maxSize = maxSize;
            StartTime = DateTime.UtcNow;
        }

        public int Count => _emails.Count;
        public DateTime StartTime { get; set; }
        public double FillRate => (double)_currentSize / _maxSize;

        public bool TryAdd(EmailInfo email)
        {
            if (_currentSize + email.Size > _maxSize && _emails.Count > 0)
            {
                return false;
            }
            
            Add(email);
            return true;
        }

        public void Add(EmailInfo email)
        {
            _emails.Add(email);
            _currentSize += email.Size;
        }

        public byte[] Serialize()
        {
            // Simple serialization - in practice would use proper format
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            writer.Write(_emails.Count);
            foreach (var email in _emails)
            {
                writer.Write(email.Id);
                writer.Write(email.Size);
                writer.Write(new byte[email.Size]); // Placeholder data
            }
            
            return ms.ToArray();
        }
    }

    private class SmartEmailBatch : EmailBatch
    {
        public SmartEmailBatch(int maxSize) : base(maxSize) { }

        public byte[] SerializeWithMetadata()
        {
            // Include metadata for each email in the batch
            var metadata = new
            {
                BatchId = Guid.NewGuid(),
                EmailCount = Count,
                StartTime = StartTime,
                EndTime = DateTime.UtcNow,
                EmailIds = _emails.Select(e => e.Id).ToArray(),
                TotalSize = _currentSize
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(metadata);
            var metadataBytes = Encoding.UTF8.GetBytes(json);
            
            var dataBytes = Serialize();
            
            var result = new byte[4 + metadataBytes.Length + dataBytes.Length];
            BitConverter.GetBytes(metadataBytes.Length).CopyTo(result, 0);
            metadataBytes.CopyTo(result, 4);
            dataBytes.CopyTo(result, 4 + metadataBytes.Length);
            
            return result;
        }
    }

    private class BatchingResult
    {
        public int BatchCount { get; set; }
        public long FileSize { get; set; }
        public double AverageFillRate { get; set; }
    }
}