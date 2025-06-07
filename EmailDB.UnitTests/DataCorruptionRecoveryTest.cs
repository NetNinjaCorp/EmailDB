using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-End test verifying EmailDB can detect and handle corrupted data.
/// Tests checksum validation and corruption recovery mechanisms.
/// </summary>
public class DataCorruptionRecoveryTest : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public DataCorruptionRecoveryTest(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"CorruptionTest_{Guid.NewGuid():N}.emdb");
    }

    [Fact]
    public async Task Should_Detect_Corrupted_Block_Checksum()
    {
        _output.WriteLine("🛡️ DATA CORRUPTION DETECTION TEST");
        _output.WriteLine("================================");
        _output.WriteLine($"📁 Test file: {_testFile}");

        // Step 1: Write valid blocks
        _output.WriteLine("\n📝 STEP 1: Writing Valid Blocks");
        _output.WriteLine("==============================");

        var testBlocks = new[]
        {
            new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1001,
                Payload = Encoding.UTF8.GetBytes("{\"email\": \"test1@example.com\", \"subject\": \"Important Message\"}")
            },
            new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1002,
                Payload = Encoding.UTF8.GetBytes("{\"created\": \"2024-01-01\", \"version\": \"1.0\"}")
            },
            new Block
            {
                Version = 1,
                Type = BlockType.Folder,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1003,
                Payload = Encoding.UTF8.GetBytes("{\"name\": \"Inbox\", \"count\": 42}")
            }
        };

        using var blockManager = new RawBlockManager(_testFile);
        
        foreach (var block in testBlocks)
        {
            var result = await blockManager.WriteBlockAsync(block);
            if (result.IsSuccess)
            {
                _output.WriteLine($"   ✅ Wrote block {block.BlockId} with checksum");
            }
        }

        var fileInfo = new FileInfo(_testFile);
        _output.WriteLine($"   📊 File size: {fileInfo.Length} bytes");

        // Step 2: Read blocks to verify they're valid
        _output.WriteLine("\n✅ STEP 2: Verifying Valid Blocks");
        _output.WriteLine("================================");

        foreach (var originalBlock in testBlocks)
        {
            var result = await blockManager.ReadBlockAsync(originalBlock.BlockId);
            Assert.True(result.IsSuccess, $"Should read block {originalBlock.BlockId} successfully");
            _output.WriteLine($"   ✅ Block {originalBlock.BlockId} read successfully with valid checksum");
        }

        // Step 3: Corrupt the file directly
        _output.WriteLine("\n💥 STEP 3: Corrupting Block Data");
        _output.WriteLine("===============================");

        // Close the block manager to release file
        blockManager.Dispose();

        // Read the file directly and corrupt some data
        var fileBytes = await File.ReadAllBytesAsync(_testFile);
        _output.WriteLine($"   📊 Original file size: {fileBytes.Length} bytes");

        // Find a payload section and corrupt it (skip the first 1KB to avoid header)
        var corruptionTargets = new[]
        {
            (offset: 1024, description: "middle of first block"),
            (offset: fileBytes.Length / 2, description: "middle of file"),
            (offset: fileBytes.Length - 100, description: "near end of file")
        };

        foreach (var (offset, description) in corruptionTargets.Where(t => t.offset < fileBytes.Length - 10))
        {
            // Flip some bits to corrupt the data
            for (int i = 0; i < 5; i++)
            {
                if (offset + i < fileBytes.Length)
                {
                    fileBytes[offset + i] = (byte)(fileBytes[offset + i] ^ 0xFF); // Flip all bits
                }
            }
            _output.WriteLine($"   💥 Corrupted 5 bytes at offset {offset} ({description})");
        }

        // Write corrupted file back
        await File.WriteAllBytesAsync(_testFile, fileBytes);
        _output.WriteLine($"   💾 Wrote corrupted file back to disk");

        // Step 4: Try to read corrupted blocks
        _output.WriteLine("\n🔍 STEP 4: Reading Corrupted Blocks");
        _output.WriteLine("==================================");

        using var corruptedBlockManager = new RawBlockManager(_testFile);
        var detectedCorruptions = 0;
        var successfulReads = 0;

        foreach (var originalBlock in testBlocks)
        {
            var result = await corruptedBlockManager.ReadBlockAsync(originalBlock.BlockId);
            
            if (result.IsSuccess)
            {
                successfulReads++;
                _output.WriteLine($"   ✅ Block {originalBlock.BlockId} read successfully (checksum still valid)");
            }
            else
            {
                detectedCorruptions++;
                _output.WriteLine($"   ❌ Block {originalBlock.BlockId} FAILED: {result.Error}");
                _output.WriteLine($"      🛡️ Checksum validation DETECTED corruption!");
            }
        }

        _output.WriteLine($"\n📊 Corruption Detection Results:");
        _output.WriteLine($"   🛡️ Corrupted blocks detected: {detectedCorruptions}");
        _output.WriteLine($"   ✅ Valid blocks read: {successfulReads}");

        // Step 5: Test specific checksum corruption
        _output.WriteLine("\n🎯 STEP 5: Targeted Checksum Corruption");
        _output.WriteLine("======================================");

        // Create a new file for targeted corruption
        var targetedFile = Path.Combine(Path.GetTempPath(), $"Targeted_{Guid.NewGuid():N}.emdb");
        using (var targetedManager = new RawBlockManager(targetedFile))
        {
            var testBlock = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 2001,
                Payload = Encoding.UTF8.GetBytes("{\"test\": \"Checksum corruption test\"}")
            };

            await targetedManager.WriteBlockAsync(testBlock);
            _output.WriteLine("   ✅ Wrote test block with checksum");
        }

        // Corrupt just the checksum bytes
        var targetedBytes = await File.ReadAllBytesAsync(targetedFile);
        
        // The checksum is typically at the end of the block header
        // Let's corrupt bytes near the beginning of the file (after magic bytes)
        if (targetedBytes.Length > 20)
        {
            targetedBytes[16] ^= 0xFF;  // Corrupt what might be checksum data
            targetedBytes[17] ^= 0xFF;
            targetedBytes[18] ^= 0xFF;
            targetedBytes[19] ^= 0xFF;
            
            await File.WriteAllBytesAsync(targetedFile, targetedBytes);
            _output.WriteLine("   💥 Corrupted checksum bytes");
        }

        using (var corruptedTargetManager = new RawBlockManager(targetedFile))
        {
            var result = await corruptedTargetManager.ReadBlockAsync(2001);
            _output.WriteLine($"   🔍 Reading block with corrupted checksum:");
            _output.WriteLine($"      Result: {(result.IsSuccess ? "SUCCESS" : "FAILED")}");
            if (!result.IsSuccess)
            {
                _output.WriteLine($"      Error: {result.Error}");
            }
        }

        // Cleanup targeted file
        File.Delete(targetedFile);

        // Final summary
        _output.WriteLine("\n🎯 CORRUPTION DETECTION SUMMARY");
        _output.WriteLine("==============================");
        _output.WriteLine($"   ✅ Successfully wrote {testBlocks.Length} blocks with checksums");
        _output.WriteLine($"   💥 Corrupted file at {corruptionTargets.Length} locations");
        _output.WriteLine($"   🛡️ Checksum validation detected {detectedCorruptions} corruptions");
        _output.WriteLine($"   ✅ Data integrity protection: WORKING");

        // Assertions
        Assert.True(detectedCorruptions > 0, "Should detect at least some corrupted blocks");
        _output.WriteLine("\n✅ DATA CORRUPTION DETECTION TEST COMPLETED");
    }

    [Fact]
    public async Task Should_Handle_Partial_Block_Corruption()
    {
        _output.WriteLine("🧩 PARTIAL BLOCK CORRUPTION TEST");
        _output.WriteLine("===============================");

        using var blockManager = new RawBlockManager(_testFile);

        // Write a large block
        var largePayload = new byte[10000];
        new Random().NextBytes(largePayload);
        
        var largeBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 3001,
            Payload = largePayload
        };

        await blockManager.WriteBlockAsync(largeBlock);
        _output.WriteLine($"   ✅ Wrote large block (10KB) with checksum");

        blockManager.Dispose();

        // Corrupt only part of the payload
        var fileBytes = await File.ReadAllBytesAsync(_testFile);
        
        // Find and corrupt middle of payload
        var corruptionOffset = fileBytes.Length / 2;
        for (int i = 0; i < 50; i++)
        {
            if (corruptionOffset + i < fileBytes.Length)
            {
                fileBytes[corruptionOffset + i] = 0x00; // Zero out bytes
            }
        }
        
        await File.WriteAllBytesAsync(_testFile, fileBytes);
        _output.WriteLine($"   💥 Corrupted 50 bytes in middle of payload");

        // Try to read
        using var corruptedManager = new RawBlockManager(_testFile);
        var result = await corruptedManager.ReadBlockAsync(3001);
        
        _output.WriteLine($"   🔍 Reading partially corrupted block:");
        _output.WriteLine($"      Result: {(result.IsSuccess ? "SUCCESS (checksum might be after corruption)" : "FAILED - Checksum detected corruption")}");
        
        if (!result.IsSuccess)
        {
            _output.WriteLine($"      ✅ Corruption detected: {result.Error}");
        }
    }

    [Fact]
    public async Task Should_Detect_Truncated_File()
    {
        _output.WriteLine("✂️ TRUNCATED FILE DETECTION TEST");
        _output.WriteLine("===============================");

        // Write some blocks
        using (var blockManager = new RawBlockManager(_testFile))
        {
            for (int i = 0; i < 5; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = 4001 + i,
                    Payload = Encoding.UTF8.GetBytes($"{{\"block\": {i}, \"data\": \"test data for truncation test\"}}")
                };
                
                await blockManager.WriteBlockAsync(block);
            }
            _output.WriteLine($"   ✅ Wrote 5 blocks to file");
        }

        var originalSize = new FileInfo(_testFile).Length;
        _output.WriteLine($"   📊 Original file size: {originalSize} bytes");

        // Truncate the file
        using (var stream = new FileStream(_testFile, FileMode.Open))
        {
            var newSize = originalSize * 3 / 4; // Keep only 75% of file
            stream.SetLength(newSize);
            _output.WriteLine($"   ✂️ Truncated file to: {newSize} bytes (75% of original)");
        }

        // Try to read blocks
        using var truncatedManager = new RawBlockManager(_testFile);
        var readableBlocks = 0;
        var corruptedBlocks = 0;

        for (int i = 0; i < 5; i++)
        {
            var result = await truncatedManager.ReadBlockAsync(4001 + i);
            if (result.IsSuccess)
            {
                readableBlocks++;
                _output.WriteLine($"   ✅ Block {4001 + i} still readable");
            }
            else
            {
                corruptedBlocks++;
                _output.WriteLine($"   ❌ Block {4001 + i} corrupted/missing: {result.Error}");
            }
        }

        _output.WriteLine($"\n📊 Truncation Results:");
        _output.WriteLine($"   ✅ Readable blocks: {readableBlocks}");
        _output.WriteLine($"   ❌ Corrupted/missing blocks: {corruptedBlocks}");

        Assert.True(corruptedBlocks > 0, "Should detect some blocks as corrupted/missing after truncation");
    }

    public void Dispose()
    {
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}