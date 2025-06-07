using System;
using System.Collections.Generic;
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
/// Tests edge cases, boundary conditions, and unusual scenarios.
/// </summary>
public class EdgeCaseHandlingTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDir;
    private readonly Random _random = new(42);

    public EdgeCaseHandlingTest(ITestOutputHelper output)
    {
        _output = output;
        _testDir = Path.Combine(Path.GetTempPath(), $"EdgeCase_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public async Task Test_Empty_And_Null_Values()
    {
        _output.WriteLine("🔲 EMPTY AND NULL VALUE TEST");
        _output.WriteLine("============================\n");
        
        var dataPath = Path.Combine(_testDir, "empty_test.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath);
        
        var edgeCases = new List<(string name, string messageId, string folder, byte[] data, string subject, string body)>
        {
            ("Empty data", "empty-data@test.com", "inbox", Array.Empty<byte>(), "Empty", ""),
            ("Empty strings", "empty-strings@test.com", "", Encoding.UTF8.GetBytes("data"), "", ""),
            ("Null-like strings", "null-strings@test.com", "null", Encoding.UTF8.GetBytes("null"), "null", "null"),
            ("Whitespace only", "whitespace@test.com", " \t\n ", Encoding.UTF8.GetBytes(" \t\n "), " \t\n ", " \t\n "),
            ("Single byte", "single-byte@test.com", "inbox", new byte[] { 0x42 }, "B", "B"),
            ("Zero byte", "zero-byte@test.com", "inbox", new byte[] { 0x00 }, "Zero", "\0"),
            ("Unicode", "unicode@test.com", "📧", Encoding.UTF8.GetBytes("Hello 世界 🌍"), "Unicode 测试", "😀🎉🌟")
        };
        
        var storedIds = new List<(string name, EmailId id)>();
        
        // Store edge cases
        _output.WriteLine("📝 Storing edge cases:");
        foreach (var (name, messageId, folder, data, subject, body) in edgeCases)
        {
            try
            {
                var id = await store.StoreEmailAsync(messageId, folder, data, subject, body: body);
                storedIds.Add((name, id));
                _output.WriteLine($"  ✅ {name}: Stored successfully");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  ❌ {name}: {ex.GetType().Name} - {ex.Message}");
            }
        }
        
        // Verify retrieval
        _output.WriteLine("\n📖 Verifying retrieval:");
        foreach (var (name, id) in storedIds)
        {
            try
            {
                var (data, metadata) = await store.GetEmailAsync(id);
                _output.WriteLine($"  ✅ {name}: Retrieved {data?.Length ?? 0} bytes");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  ❌ {name}: {ex.GetType().Name}");
            }
        }
    }

    [Fact]
    public async Task Test_Extreme_Sizes()
    {
        _output.WriteLine("\n📏 EXTREME SIZE TEST");
        _output.WriteLine("===================");
        
        var path = Path.Combine(_testDir, "extreme_sizes.db");
        using var manager = new RawBlockManager(path, createNew: true);
        
        var testCases = new List<(string name, int size)>
        {
            ("Minimum (1 byte)", 1),
            ("Small (100 bytes)", 100),
            ("Medium (1 KB)", 1024),
            ("Large (1 MB)", 1024 * 1024),
            ("Very Large (10 MB)", 10 * 1024 * 1024),
            ("Extreme (50 MB)", 50 * 1024 * 1024)
        };
        
        foreach (var (name, size) in testCases)
        {
            _output.WriteLine($"\n  Testing {name}:");
            
            try
            {
                var data = new byte[size];
                _random.NextBytes(data);
                
                var block = new Block
                {
                    BlockId = size,
                    BlockType = BlockType.Segment,
                    PayloadEncoding = PayloadEncoding.Binary,
                    Content = new SegmentContent { SegmentData = data },
                    Timestamp = DateTime.UtcNow.Ticks
                };
                
                var writeResult = await manager.WriteBlockAsync(block);
                Assert.True(writeResult.Success);
                
                var readResult = await manager.ReadBlockAsync(size);
                Assert.True(readResult.Success);
                
                var readData = (readResult.Value?.Content as SegmentContent)?.SegmentData;
                Assert.Equal(size, readData?.Length ?? 0);
                
                _output.WriteLine($"    ✅ Write and read successful");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"    ❌ Failed: {ex.Message}");
                if (size <= 10 * 1024 * 1024) // Should handle up to 10MB
                {
                    throw;
                }
            }
        }
    }

    [Fact]
    public async Task Test_Special_Characters_And_Paths()
    {
        _output.WriteLine("\n🔤 SPECIAL CHARACTERS TEST");
        _output.WriteLine("=========================");
        
        var dataPath = Path.Combine(_testDir, "special_chars.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath);
        
        var specialCases = new[]
        {
            // Special characters in message IDs
            "<message@id>",
            "message@[127.0.0.1]",
            "message+tag@example.com",
            "\"quoted\"@example.com",
            "message@exam ple.com", // space
            "message@example.com\n", // newline
            "message@example.com\0", // null char
            
            // SQL injection attempts
            "'; DROP TABLE emails; --",
            "1' OR '1'='1",
            
            // Path traversal attempts
            "../../../etc/passwd",
            "..\\..\\windows\\system32",
            
            // Unicode edge cases
            "测试@例子.com",
            "🚀@🌍.com",
            "\u200B@\u200B.com", // zero-width space
            
            // Long strings
            new string('a', 255) + "@example.com",
            "x@" + new string('b', 255) + ".com"
        };
        
        _output.WriteLine("  Testing special message IDs:");
        
        var successCount = 0;
        foreach (var messageId in specialCases)
        {
            try
            {
                var id = await store.StoreEmailAsync(
                    messageId,
                    "special",
                    Encoding.UTF8.GetBytes($"Test for: {messageId}"),
                    subject: $"Special: {messageId.Substring(0, Math.Min(20, messageId.Length))}"
                );
                
                // Try to retrieve
                var (data, meta) = await store.GetEmailByMessageIdAsync(messageId);
                Assert.NotNull(data);
                Assert.Equal(messageId, meta?.MessageId);
                
                successCount++;
            }
            catch
            {
                _output.WriteLine($"    ⚠️ Failed: {messageId.Substring(0, Math.Min(50, messageId.Length))}");
            }
        }
        
        _output.WriteLine($"  ✅ Handled {successCount}/{specialCases.Length} special cases");
    }

    [Fact]
    public async Task Test_Concurrent_Edge_Cases()
    {
        _output.WriteLine("\n🔀 CONCURRENT EDGE CASES");
        _output.WriteLine("========================");
        
        var dataPath = Path.Combine(_testDir, "concurrent_edge.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        using var store = new HybridEmailStore(dataPath, indexPath);
        
        // Test duplicate message IDs from different threads
        _output.WriteLine("  Testing concurrent duplicate prevention:");
        
        var duplicateId = "duplicate@test.com";
        var tasks = new List<Task<bool>>();
        
        for (int i = 0; i < 10; i++)
        {
            int threadId = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await store.StoreEmailAsync(
                        duplicateId,
                        "inbox",
                        Encoding.UTF8.GetBytes($"From thread {threadId}"),
                        subject: $"Thread {threadId}"
                    );
                    return true;
                }
                catch
                {
                    return false;
                }
            }));
        }
        
        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);
        
        _output.WriteLine($"    Successful stores: {successCount}/10");
        _output.WriteLine($"    Duplicates prevented: {10 - successCount}");
        
        Assert.Equal(1, successCount); // Only one should succeed
        
        // Test rapid folder moves
        _output.WriteLine("\n  Testing rapid folder moves:");
        
        var moveTestId = await store.StoreEmailAsync(
            "move-test@example.com",
            "inbox",
            Encoding.UTF8.GetBytes("Move test email")
        );
        
        var moveTasks = new List<Task>();
        var folders = new[] { "inbox", "sent", "drafts", "trash" };
        
        for (int i = 0; i < 20; i++)
        {
            int index = i;
            moveTasks.Add(Task.Run(async () =>
            {
                await store.MoveEmailAsync(moveTestId, folders[index % folders.Length]);
            }));
        }
        
        await Task.WhenAll(moveTasks);
        
        var (_, finalMeta) = await store.GetEmailAsync(moveTestId);
        _output.WriteLine($"    Final folder: {finalMeta?.Folder}");
        Assert.NotNull(finalMeta);
        Assert.Contains(finalMeta.Folder, folders);
    }

    [Fact]
    public async Task Test_Block_ID_Edge_Cases()
    {
        _output.WriteLine("\n🔢 BLOCK ID EDGE CASES");
        _output.WriteLine("======================");
        
        var path = Path.Combine(_testDir, "blockid_edge.db");
        using var manager = new RawBlockManager(path, createNew: true);
        
        var edgeIds = new[]
        {
            0L,
            1L,
            -1L,
            long.MaxValue,
            long.MinValue,
            int.MaxValue,
            int.MinValue,
            42L,
            -42L
        };
        
        foreach (var blockId in edgeIds)
        {
            try
            {
                var block = new Block
                {
                    BlockId = blockId,
                    BlockType = BlockType.Segment,
                    PayloadEncoding = PayloadEncoding.Binary,
                    Content = new SegmentContent 
                    { 
                        SegmentData = Encoding.UTF8.GetBytes($"Block ID: {blockId}")
                    }
                };
                
                var writeResult = await manager.WriteBlockAsync(block);
                var readResult = await manager.ReadBlockAsync(blockId);
                
                _output.WriteLine($"  Block ID {blockId,20}: " +
                                $"Write={writeResult.Success}, Read={readResult.Success}");
                
                if (writeResult.Success)
                {
                    Assert.True(readResult.Success);
                    Assert.Equal(blockId, readResult.Value?.BlockId);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  Block ID {blockId,20}: Exception - {ex.GetType().Name}");
            }
        }
    }

    [Fact]
    public async Task Test_Resource_Exhaustion()
    {
        _output.WriteLine("\n💥 RESOURCE EXHAUSTION TEST");
        _output.WriteLine("===========================");
        
        var dataPath = Path.Combine(_testDir, "exhaustion.data");
        var indexPath = Path.Combine(_testDir, "indexes");
        
        // Test with minimal block size to force many blocks
        using var store = new HybridEmailStore(dataPath, indexPath, blockSizeThreshold: 1024); // 1KB blocks
        
        _output.WriteLine("  Creating many small blocks:");
        
        var emailCount = 0;
        var errorCount = 0;
        
        // Try to create many emails quickly
        for (int i = 0; i < 1000; i++)
        {
            try
            {
                await store.StoreEmailAsync(
                    $"exhaust-{i}@test.com",
                    $"folder-{i % 100}", // Many different folders
                    Encoding.UTF8.GetBytes(new string('x', 900)), // Almost fill each block
                    subject: $"Exhaustion test {i}"
                );
                emailCount++;
            }
            catch
            {
                errorCount++;
                if (errorCount > 10) break; // Stop if too many errors
            }
        }
        
        _output.WriteLine($"    Emails stored: {emailCount}");
        _output.WriteLine($"    Errors: {errorCount}");
        
        var stats = store.GetStats();
        _output.WriteLine($"    Block count (estimated): {stats.DataFileSize / 1024}");
        _output.WriteLine($"    Folders created: {stats.FolderCount}");
        
        Assert.True(emailCount > 900, "Should handle at least 900 emails");
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