using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests cross-format compatibility and data migration scenarios.
/// </summary>
public class CrossFormatCompatibilityTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public CrossFormatCompatibilityTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"CrossFormat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_RawBlockManager_To_HybridStore_Migration()
    {
        _output.WriteLine("🔄 CROSS-FORMAT COMPATIBILITY TEST");
        _output.WriteLine("==================================\n");
        
        // Phase 1: Write data using RawBlockManager
        _output.WriteLine("📝 PHASE 1: WRITE WITH RAW BLOCK MANAGER");
        _output.WriteLine("========================================");
        
        var rawPath = Path.Combine(_testDir, "raw_blocks.db");
        using var rawManager = new RawBlockManager(rawPath, createNew: true);
        
        var blockIds = new List<long>();
        var blockData = new Dictionary<long, string>();
        
        for (int i = 0; i < 100; i++)
        {
            var content = new SegmentContent
            {
                SegmentData = Encoding.UTF8.GetBytes($"Email content {i}: " + new string('x', _random.Next(1000, 5000)))
            };
            
            var block = new Block
            {
                BlockId = i + 1000,
                BlockType = BlockType.Segment,
                PayloadEncoding = PayloadEncoding.Binary,
                Timestamp = DateTime.UtcNow.Ticks,
                Content = content,
                PayloadLength = content.SegmentData.Length
            };
            
            var result = await rawManager.WriteBlockAsync(block);
            Assert.True(result.Success);
            
            blockIds.Add(block.BlockId);
            blockData[block.BlockId] = Encoding.UTF8.GetString(content.SegmentData);
        }
        
        rawManager.Dispose();
        
        _output.WriteLine($"  Blocks written: {blockIds.Count}");
        _output.WriteLine($"  File size: {FormatBytes(new FileInfo(rawPath).Length)}");
        
        // Phase 2: Read data back with another RawBlockManager
        _output.WriteLine("\n📖 PHASE 2: VERIFY WITH NEW RAW BLOCK MANAGER");
        _output.WriteLine("=============================================");
        
        using var rawReader = new RawBlockManager(rawPath, createNew: false);
        var locations = rawReader.GetBlockLocations();
        
        _output.WriteLine($"  Blocks found: {locations.Count}");
        
        var readErrors = 0;
        foreach (var blockId in blockIds.Take(10)) // Sample verification
        {
            var result = await rawReader.ReadBlockAsync(blockId);
            if (!result.Success || result.Value == null)
            {
                readErrors++;
                continue;
            }
            
            var content = result.Value.Content as SegmentContent;
            var data = Encoding.UTF8.GetString(content?.SegmentData ?? Array.Empty<byte>());
            
            if (data != blockData[blockId])
            {
                readErrors++;
            }
        }
        
        _output.WriteLine($"  Sample verification errors: {readErrors}");
        
        // Phase 3: Create metadata and folder structure
        _output.WriteLine("\n🗂️ PHASE 3: CREATE METADATA STRUCTURE");
        _output.WriteLine("====================================");
        
        var metadataPath = Path.Combine(_testDir, "metadata.db");
        using var metadataManager = new MetadataManager(metadataPath);
        
        // Create folder structure
        var folders = new[] { "inbox", "sent", "archive" };
        foreach (var folder in folders)
        {
            metadataManager.CreateFolder(folder);
        }
        
        // Add block references to folders
        for (int i = 0; i < blockIds.Count; i++)
        {
            var folder = folders[i % folders.Length];
            metadataManager.AddEmailToFolder(folder, blockIds[i]);
        }
        
        _output.WriteLine($"  Folders created: {folders.Length}");
        _output.WriteLine($"  Blocks indexed: {blockIds.Count}");
        
        // Phase 4: Migrate to HybridStore format
        _output.WriteLine("\n🔀 PHASE 4: MIGRATE TO HYBRID STORE");
        _output.WriteLine("===================================");
        
        var hybridDataPath = Path.Combine(_testDir, "hybrid.data");
        var hybridIndexPath = Path.Combine(_testDir, "indexes");
        
        using var hybridStore = new HybridEmailStore(hybridDataPath, hybridIndexPath);
        
        var migrationErrors = 0;
        var migratedCount = 0;
        
        var sw = Stopwatch.StartNew();
        
        // Read from raw blocks and write to hybrid store
        foreach (var (blockId, i) in blockIds.Select((id, idx) => (id, idx)))
        {
            try
            {
                var result = await rawReader.ReadBlockAsync(blockId);
                if (!result.Success || result.Value == null) continue;
                
                var content = result.Value.Content as SegmentContent;
                if (content == null) continue;
                
                var messageId = $"migrated-{blockId}@example.com";
                var folder = folders[i % folders.Length];
                
                await hybridStore.StoreEmailAsync(
                    messageId,
                    folder,
                    content.SegmentData,
                    subject: $"Migrated email {blockId}",
                    from: "migration@system.local",
                    body: Encoding.UTF8.GetString(content.SegmentData)
                );
                
                migratedCount++;
            }
            catch (Exception ex)
            {
                migrationErrors++;
                _output.WriteLine($"  Migration error for block {blockId}: {ex.Message}");
            }
        }
        
        await hybridStore.FlushAsync();
        sw.Stop();
        
        _output.WriteLine($"  Migration time: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"  Migrated: {migratedCount}/{blockIds.Count}");
        _output.WriteLine($"  Errors: {migrationErrors}");
        
        // Phase 5: Verify migrated data
        _output.WriteLine("\n✅ PHASE 5: VERIFY MIGRATED DATA");
        _output.WriteLine("================================");
        
        var verifyErrors = 0;
        
        // Verify by message ID
        foreach (var blockId in blockIds.Take(20))
        {
            try
            {
                var messageId = $"migrated-{blockId}@example.com";
                var (data, metadata) = await hybridStore.GetEmailByMessageIdAsync(messageId);
                
                if (data == null || metadata == null)
                {
                    verifyErrors++;
                    continue;
                }
                
                var originalData = blockData[blockId];
                var migratedData = Encoding.UTF8.GetString(data);
                
                if (!migratedData.Contains(originalData.Substring(0, Math.Min(50, originalData.Length))))
                {
                    verifyErrors++;
                }
            }
            catch
            {
                verifyErrors++;
            }
        }
        
        // Verify folder structure
        var folderCounts = new Dictionary<string, int>();
        foreach (var folder in folders)
        {
            folderCounts[folder] = hybridStore.ListFolder(folder).Count();
        }
        
        _output.WriteLine($"  Verification errors: {verifyErrors}");
        _output.WriteLine($"  Folder distribution:");
        foreach (var (folder, count) in folderCounts)
        {
            _output.WriteLine($"    {folder}: {count} emails");
        }
        
        // Summary
        _output.WriteLine("\n📊 MIGRATION SUMMARY");
        _output.WriteLine("===================");
        _output.WriteLine($"  Source blocks: {blockIds.Count}");
        _output.WriteLine($"  Successfully migrated: {migratedCount}");
        _output.WriteLine($"  Migration errors: {migrationErrors}");
        _output.WriteLine($"  Verification errors: {verifyErrors}");
        _output.WriteLine($"  Success rate: {(migratedCount * 100.0 / blockIds.Count):F1}%");
        
        Assert.True(migratedCount > blockIds.Count * 0.95, "Migration success rate too low");
        Assert.True(verifyErrors < migratedCount * 0.05, "Too many verification errors");
    }

    [Fact]
    public async Task Test_PayloadEncoding_Compatibility()
    {
        _output.WriteLine("\n🔢 PAYLOAD ENCODING COMPATIBILITY TEST");
        _output.WriteLine("=====================================");
        
        var path = Path.Combine(_testDir, "encoding_test.db");
        using var manager = new RawBlockManager(path, createNew: true);
        
        var testData = new Dictionary<PayloadEncoding, (Block block, string expectedData)>();
        
        // Test different encoding types
        var encodings = new[]
        {
            (PayloadEncoding.Binary, "Binary test data with special chars: \0\x01\x02"),
            (PayloadEncoding.Json, "{\"test\": \"JSON data\", \"value\": 123}"),
            (PayloadEncoding.MessagePack, "MessagePack compatible data"),
            (PayloadEncoding.Protobuf, "Protobuf compatible data"),
            (PayloadEncoding.Brotli, "Data to be compressed with Brotli"),
            (PayloadEncoding.Gzip, "Data to be compressed with Gzip"),
            (PayloadEncoding.Zstd, "Data to be compressed with Zstd")
        };
        
        // Write blocks with different encodings
        foreach (var (encoding, data) in encodings)
        {
            var content = new SegmentContent
            {
                SegmentData = Encoding.UTF8.GetBytes(data)
            };
            
            var block = new Block
            {
                BlockId = (long)encoding,
                BlockType = BlockType.Segment,
                PayloadEncoding = encoding,
                Timestamp = DateTime.UtcNow.Ticks,
                Content = content
            };
            
            var result = await manager.WriteBlockAsync(block);
            Assert.True(result.Success);
            
            testData[encoding] = (block, data);
        }
        
        manager.Dispose();
        
        // Read back with new instance
        using var reader = new RawBlockManager(path, createNew: false);
        
        var encodingErrors = 0;
        foreach (var (encoding, (originalBlock, expectedData)) in testData)
        {
            var result = await reader.ReadBlockAsync((long)encoding);
            Assert.True(result.Success);
            
            var block = result.Value;
            Assert.NotNull(block);
            Assert.Equal(encoding, block.PayloadEncoding);
            
            var content = block.Content as SegmentContent;
            var actualData = Encoding.UTF8.GetString(content?.SegmentData ?? Array.Empty<byte>());
            
            if (actualData != expectedData)
            {
                encodingErrors++;
                _output.WriteLine($"  ❌ Encoding mismatch for {encoding}");
            }
            else
            {
                _output.WriteLine($"  ✅ {encoding} preserved correctly");
            }
        }
        
        Assert.Equal(0, encodingErrors);
    }

    [Fact]
    public async Task Test_BlockType_Compatibility()
    {
        _output.WriteLine("\n📦 BLOCK TYPE COMPATIBILITY TEST");
        _output.WriteLine("================================");
        
        var path = Path.Combine(_testDir, "blocktype_test.db");
        using var manager = new RawBlockManager(path, createNew: true);
        
        // Test all block types
        var blockTypes = new Dictionary<BlockType, BlockContent>
        {
            [BlockType.Header] = new HeaderContent { Version = "1.0", CreatedTimestamp = DateTime.UtcNow.Ticks },
            [BlockType.Metadata] = new MetadataContent(),
            [BlockType.FolderTree] = new FolderTreeContent(),
            [BlockType.Folder] = new FolderContent { Name = "test-folder" },
            [BlockType.Segment] = new SegmentContent { SegmentData = Encoding.UTF8.GetBytes("Segment data") },
            [BlockType.WAL] = new WALContent()
        };
        
        // Write blocks of each type
        foreach (var (blockType, content) in blockTypes)
        {
            var block = new Block
            {
                BlockId = (long)blockType,
                BlockType = blockType,
                PayloadEncoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                Content = content
            };
            
            var result = await manager.WriteBlockAsync(block);
            Assert.True(result.Success);
        }
        
        manager.Dispose();
        
        // Read back and verify
        using var reader = new RawBlockManager(path, createNew: false);
        
        foreach (var (blockType, _) in blockTypes)
        {
            var result = await reader.ReadBlockAsync((long)blockType);
            Assert.True(result.Success);
            
            var block = result.Value;
            Assert.NotNull(block);
            Assert.Equal(blockType, block.BlockType);
            
            _output.WriteLine($"  ✅ {blockType} preserved correctly");
        }
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
}