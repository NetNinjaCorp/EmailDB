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
/// Analyzes storage overhead for different integrity/recovery mechanisms.
/// </summary>
public class StorageOverheadAnalysisTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _testFiles = new();

    public StorageOverheadAnalysisTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Analyze_Storage_Overhead_For_Different_Approaches()
    {
        _output.WriteLine("📊 STORAGE OVERHEAD ANALYSIS");
        _output.WriteLine("===========================");
        _output.WriteLine("Comparing file size impact of different integrity/recovery mechanisms\n");

        // Test parameters
        const int emailCount = 100;
        const int emailSizeBytes = 2048; // 2KB average email
        const int updateCount = 20; // 20% of emails get updated
        
        // Generate test emails
        var emails = GenerateTestEmails(emailCount, emailSizeBytes);
        
        // Test 1: Baseline - No extra features
        var baselineSize = await TestBaseline(emails);
        
        // Test 2: With Hash Chain
        var hashChainSize = await TestWithHashChain(emails);
        
        // Test 3: With Checkpoints
        var checkpointSize = await TestWithCheckpoints(emails, updateCount);
        
        // Test 4: With WAL (simulated)
        var walSize = await TestWithWAL(emails, updateCount);
        
        // Test 5: Combined (Hash Chain + Checkpoints)
        var combinedSize = await TestCombined(emails, updateCount);

        // Summary
        _output.WriteLine("\n📈 FINAL ANALYSIS");
        _output.WriteLine("================");
        _output.WriteLine($"Baseline size:                {FormatBytes(baselineSize)} (100%)");
        _output.WriteLine($"With Hash Chain:             {FormatBytes(hashChainSize)} ({CalculatePercentage(hashChainSize, baselineSize):F1}% - {FormatBytes(hashChainSize - baselineSize)} overhead)");
        _output.WriteLine($"With Checkpoints:            {FormatBytes(checkpointSize)} ({CalculatePercentage(checkpointSize, baselineSize):F1}% - {FormatBytes(checkpointSize - baselineSize)} overhead)");
        _output.WriteLine($"With WAL:                    {FormatBytes(walSize)} ({CalculatePercentage(walSize, baselineSize):F1}% - {FormatBytes(walSize - baselineSize)} overhead)");
        _output.WriteLine($"Combined (Chain+Checkpoint): {FormatBytes(combinedSize)} ({CalculatePercentage(combinedSize, baselineSize):F1}% - {FormatBytes(combinedSize - baselineSize)} overhead)");
        
        _output.WriteLine("\n💡 RECOMMENDATIONS:");
        var minOverhead = Math.Min(Math.Min(hashChainSize, checkpointSize), walSize) - baselineSize;
        if (hashChainSize - baselineSize == minOverhead)
        {
            _output.WriteLine("✅ Hash Chain has the LOWEST overhead - good for archival with integrity");
        }
        if (checkpointSize - baselineSize == minOverhead)
        {
            _output.WriteLine("✅ Checkpoints have the LOWEST overhead - good for recovery without full duplication");
        }
        if (walSize - baselineSize == minOverhead)
        {
            _output.WriteLine("✅ WAL has the LOWEST overhead - good for transaction safety");
        }
        
        // Compaction analysis
        _output.WriteLine("\n🗜️ COMPACTION POTENTIAL:");
        var compactableSpace = checkpointSize - baselineSize;
        _output.WriteLine($"If using checkpoints, compaction could save up to {FormatBytes(compactableSpace)}");
        _output.WriteLine($"That's {CalculatePercentage(compactableSpace, checkpointSize):F1}% of the checkpoint-enabled file size");
    }

    private async Task<long> TestBaseline(List<TestEmail> emails)
    {
        _output.WriteLine("\n📁 TEST 1: BASELINE (No extra features)");
        _output.WriteLine("=====================================");
        
        var file = CreateTestFile("baseline");
        using (var blockManager = new RawBlockManager(file))
        {
            foreach (var email in emails)
            {
                var block = CreateEmailBlock(email);
                await blockManager.WriteBlockAsync(block);
            }
        }
        
        var size = new FileInfo(file).Length;
        _output.WriteLine($"   File size: {FormatBytes(size)}");
        _output.WriteLine($"   Blocks written: {emails.Count}");
        _output.WriteLine($"   Average block size: {FormatBytes(size / emails.Count)}");
        
        return size;
    }

    private async Task<long> TestWithHashChain(List<TestEmail> emails)
    {
        _output.WriteLine("\n🔗 TEST 2: WITH HASH CHAIN");
        _output.WriteLine("=========================");
        
        var file = CreateTestFile("hashchain");
        using (var blockManager = new RawBlockManager(file))
        {
            var hashChainManager = new HashChainManager(blockManager);
            
            foreach (var email in emails)
            {
                var block = CreateEmailBlock(email);
                await blockManager.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
            }
        }
        
        var size = new FileInfo(file).Length;
        _output.WriteLine($"   File size: {FormatBytes(size)}");
        _output.WriteLine($"   Hash chain entries: {emails.Count}");
        
        // Calculate hash chain overhead
        using (var blockManager = new RawBlockManager(file, createIfNotExists: false))
        {
            var locations = blockManager.GetBlockLocations();
            var hashChainBlocks = locations.Count(l => l.Key >= 2_000_000_000_000);
            _output.WriteLine($"   Hash chain blocks: {hashChainBlocks}");
        }
        
        return size;
    }

    private async Task<long> TestWithCheckpoints(List<TestEmail> emails, int updateCount)
    {
        _output.WriteLine("\n💾 TEST 3: WITH CHECKPOINTS");
        _output.WriteLine("==========================");
        
        var file = CreateTestFile("checkpoint");
        var blockIds = new List<long>();
        
        using (var blockManager = new RawBlockManager(file))
        {
            var checkpointManager = new CheckpointManager(blockManager);
            
            // Write original emails
            foreach (var email in emails)
            {
                var block = CreateEmailBlock(email);
                await blockManager.WriteBlockAsync(block);
                blockIds.Add(block.BlockId);
            }
            
            // Create checkpoints for 30% of emails (simulating important emails)
            var importantCount = (int)(emails.Count * 0.3);
            for (int i = 0; i < importantCount; i++)
            {
                await checkpointManager.CreateCheckpointAsync((ulong)blockIds[i]);
            }
            
            // Simulate updates
            for (int i = 0; i < updateCount; i++)
            {
                var originalEmail = emails[i];
                originalEmail.Subject += " (Updated)";
                var updateBlock = CreateEmailBlock(originalEmail);
                updateBlock.BlockId = 10000 + i; // New block ID
                await blockManager.WriteBlockAsync(updateBlock);
                
                // Checkpoint the update
                await checkpointManager.CreateCheckpointAsync((ulong)updateBlock.BlockId);
            }
        }
        
        var size = new FileInfo(file).Length;
        _output.WriteLine($"   File size: {FormatBytes(size)}");
        _output.WriteLine($"   Original blocks: {emails.Count}");
        _output.WriteLine($"   Checkpointed blocks: {(int)(emails.Count * 0.3)}");
        _output.WriteLine($"   Updates: {updateCount}");
        
        return size;
    }

    private async Task<long> TestWithWAL(List<TestEmail> emails, int updateCount)
    {
        _output.WriteLine("\n📝 TEST 4: WITH WAL (Write-Ahead Log)");
        _output.WriteLine("====================================");
        
        var file = CreateTestFile("wal");
        var walFile = file + ".wal";
        
        using (var blockManager = new RawBlockManager(file))
        {
            // Simulate WAL entries for each write
            var walEntries = new List<byte[]>();
            
            foreach (var email in emails)
            {
                var block = CreateEmailBlock(email);
                
                // WAL entry (simulated - in reality ZoneTree handles this)
                var walEntry = CreateWALEntry(block);
                walEntries.Add(walEntry);
                
                await blockManager.WriteBlockAsync(block);
            }
            
            // Write WAL file
            await File.WriteAllBytesAsync(walFile, walEntries.SelectMany(e => e).ToArray());
        }
        
        var mainSize = new FileInfo(file).Length;
        var walSize = new FileInfo(walFile).Length;
        var totalSize = mainSize + walSize;
        
        _output.WriteLine($"   Main file size: {FormatBytes(mainSize)}");
        _output.WriteLine($"   WAL file size: {FormatBytes(walSize)}");
        _output.WriteLine($"   Total size: {FormatBytes(totalSize)}");
        _output.WriteLine($"   WAL overhead: {CalculatePercentage(walSize, mainSize):F1}% of main file");
        
        _testFiles.Add(walFile); // For cleanup
        return totalSize;
    }

    private async Task<long> TestCombined(List<TestEmail> emails, int updateCount)
    {
        _output.WriteLine("\n🔄 TEST 5: COMBINED (Hash Chain + Checkpoints)");
        _output.WriteLine("============================================");
        
        var file = CreateTestFile("combined");
        
        using (var blockManager = new RawBlockManager(file))
        {
            var hashChainManager = new HashChainManager(blockManager);
            var checkpointManager = new CheckpointManager(blockManager);
            
            // Write with both hash chain and selective checkpoints
            for (int i = 0; i < emails.Count; i++)
            {
                var email = emails[i];
                var block = CreateEmailBlock(email);
                
                await blockManager.WriteBlockAsync(block);
                await hashChainManager.AddToChainAsync(block);
                
                // Checkpoint every 10th email
                if (i % 10 == 0)
                {
                    await checkpointManager.CreateCheckpointAsync((ulong)block.BlockId);
                }
            }
        }
        
        var size = new FileInfo(file).Length;
        _output.WriteLine($"   File size: {FormatBytes(size)}");
        _output.WriteLine($"   Emails: {emails.Count}");
        _output.WriteLine($"   Checkpointed: {emails.Count / 10}");
        
        return size;
    }

    private List<TestEmail> GenerateTestEmails(int count, int avgSize)
    {
        var emails = new List<TestEmail>();
        var random = new Random(42); // Fixed seed for reproducibility
        
        for (int i = 0; i < count; i++)
        {
            var contentSize = avgSize - 200; // Leave room for metadata
            var content = GenerateRandomContent(contentSize, random);
            
            emails.Add(new TestEmail
            {
                Id = i,
                From = $"user{i}@example.com",
                To = $"recipient{i % 10}@example.com",
                Subject = $"Test Email {i} - {GenerateRandomContent(30, random)}",
                Body = content,
                Timestamp = DateTime.UtcNow.AddDays(-random.Next(365))
            });
        }
        
        return emails;
    }

    private string GenerateRandomContent(int size, Random random)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,!?\n";
        var result = new char[size];
        for (int i = 0; i < size; i++)
        {
            result[i] = chars[random.Next(chars.Length)];
        }
        return new string(result);
    }

    private Block CreateEmailBlock(TestEmail email)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(email);
        return new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.Json,
            Timestamp = email.Timestamp.Ticks,
            BlockId = email.Id,
            Payload = Encoding.UTF8.GetBytes(json)
        };
    }

    private byte[] CreateWALEntry(Block block)
    {
        // Simulate a WAL entry structure
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(block.BlockId);
        writer.Write(block.Timestamp);
        writer.Write((byte)block.Type);
        writer.Write(block.Payload.Length);
        writer.Write(block.Payload);
        writer.Write(0xDEADBEEF); // WAL entry marker
        
        return ms.ToArray();
    }

    private string CreateTestFile(string suffix)
    {
        var file = Path.Combine(Path.GetTempPath(), $"StorageTest_{suffix}_{Guid.NewGuid():N}.emdb");
        _testFiles.Add(file);
        return file;
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

    private double CalculatePercentage(long value, long baseline)
    {
        return (double)value / baseline * 100;
    }

    public void Dispose()
    {
        foreach (var file in _testFiles)
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best effort
                }
            }
        }
    }

    private class TestEmail
    {
        public long Id { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

/// <summary>
/// Analyzes the impact of compaction on file size.
/// </summary>
public class CompactionAnalysisTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _testFiles = new();

    public CompactionAnalysisTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Analyze_Compaction_Benefits()
    {
        _output.WriteLine("🗜️ COMPACTION ANALYSIS");
        _output.WriteLine("====================");
        _output.WriteLine("Analyzing potential space savings from compaction\n");

        const int emailCount = 1000;
        const int updateIterations = 5; // Each email updated 5 times
        
        var file = CreateTestFile("compaction");
        var blockIds = new List<long>();
        
        // Phase 1: Create initial database with many updates
        _output.WriteLine("📝 PHASE 1: Creating database with many updates");
        _output.WriteLine("============================================");
        
        long originalDataSize = 0;
        long totalWrittenSize = 0;
        
        using (var blockManager = new RawBlockManager(file))
        {
            // Write original emails
            for (int i = 0; i < emailCount; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = i,
                    Payload = Encoding.UTF8.GetBytes($"{{\"id\":{i},\"subject\":\"Original Email {i}\",\"body\":\"Content...\"}}")
                };
                
                await blockManager.WriteBlockAsync(block);
                blockIds.Add(block.BlockId);
                originalDataSize += block.Payload.Length + 61; // 61 bytes overhead per block
            }
            
            _output.WriteLine($"   Original emails written: {emailCount}");
            _output.WriteLine($"   Original data size: {FormatBytes(originalDataSize)}");
            
            // Simulate updates (append-only means old versions remain)
            for (int iteration = 1; iteration <= updateIterations; iteration++)
            {
                _output.WriteLine($"\n   Update iteration {iteration}:");
                
                for (int i = 0; i < emailCount; i++)
                {
                    if (i % (iteration + 1) == 0) // Different emails updated each iteration
                    {
                        var updateBlock = new Block
                        {
                            Version = 1,
                            Type = BlockType.Segment,
                            Flags = 0x10, // Update flag
                            Encoding = PayloadEncoding.Json,
                            Timestamp = DateTime.UtcNow.Ticks,
                            BlockId = 10000 * iteration + i,
                            Payload = Encoding.UTF8.GetBytes($"{{\"id\":{i},\"subject\":\"Updated Email {i} v{iteration}\",\"body\":\"Updated content iteration {iteration}...\"}}")
                        };
                        
                        await blockManager.WriteBlockAsync(updateBlock);
                        totalWrittenSize += updateBlock.Payload.Length + 61;
                    }
                }
                
                var updatedCount = emailCount / (iteration + 1);
                _output.WriteLine($"      Emails updated: {updatedCount}");
            }
        }
        
        var beforeSize = new FileInfo(file).Length;
        var wastedSpace = beforeSize - originalDataSize;
        
        _output.WriteLine($"\n📊 BEFORE COMPACTION:");
        _output.WriteLine($"   File size: {FormatBytes(beforeSize)}");
        _output.WriteLine($"   Active data: {FormatBytes(originalDataSize)}");
        _output.WriteLine($"   Obsolete data: {FormatBytes(wastedSpace)}");
        _output.WriteLine($"   Space efficiency: {CalculatePercentage(originalDataSize, beforeSize):F1}%");
        
        // Phase 2: Simulate compaction
        _output.WriteLine("\n🔄 PHASE 2: Simulating Compaction");
        _output.WriteLine("================================");
        
        var compactedFile = CreateTestFile("compacted");
        
        using (var oldManager = new RawBlockManager(file, createIfNotExists: false))
        using (var newManager = new RawBlockManager(compactedFile))
        {
            // In a real compaction, we would:
            // 1. Track current version of each logical record
            // 2. Copy only the current versions to new file
            // 3. Update indexes
            
            // For simulation, copy only original blocks (representing current versions)
            foreach (var blockId in blockIds)
            {
                var result = await oldManager.ReadBlockAsync(blockId);
                if (result.IsSuccess && result.Value != null)
                {
                    await newManager.WriteBlockAsync(result.Value);
                }
            }
        }
        
        var afterSize = new FileInfo(compactedFile).Length;
        var savedSpace = beforeSize - afterSize;
        
        _output.WriteLine($"\n📊 AFTER COMPACTION:");
        _output.WriteLine($"   New file size: {FormatBytes(afterSize)}");
        _output.WriteLine($"   Space saved: {FormatBytes(savedSpace)}");
        _output.WriteLine($"   Reduction: {CalculatePercentage(savedSpace, beforeSize):F1}%");
        _output.WriteLine($"   Space efficiency: {CalculatePercentage(originalDataSize, afterSize):F1}%");
        
        // Analysis
        _output.WriteLine($"\n💡 COMPACTION INSIGHTS:");
        _output.WriteLine($"   Average updates per email: {updateIterations / 2.0:F1}");
        _output.WriteLine($"   Space amplification factor: {beforeSize / (double)originalDataSize:F2}x");
        _output.WriteLine($"   Compaction recovery rate: {CalculatePercentage(savedSpace, wastedSpace):F1}% of wasted space");
        
        if (savedSpace > beforeSize * 0.3)
        {
            _output.WriteLine($"\n✅ RECOMMENDATION: Compaction would save {CalculatePercentage(savedSpace, beforeSize):F0}% - highly beneficial!");
        }
        else
        {
            _output.WriteLine($"\n⚠️ RECOMMENDATION: Compaction would only save {CalculatePercentage(savedSpace, beforeSize):F0}% - may not be worth the effort");
        }
    }

    private string CreateTestFile(string suffix)
    {
        var file = Path.Combine(Path.GetTempPath(), $"CompactionTest_{suffix}_{Guid.NewGuid():N}.emdb");
        _testFiles.Add(file);
        return file;
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

    private double CalculatePercentage(long value, long baseline)
    {
        return (double)value / baseline * 100;
    }

    public void Dispose()
    {
        foreach (var file in _testFiles)
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best effort
                }
            }
        }
    }
}