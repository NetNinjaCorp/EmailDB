using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Tests for EmailDB block cleanup and compaction functionality.
/// Verifies that old block versions are properly removed and space is reclaimed.
/// </summary>
public class CleanupTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public CleanupTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task CompactAsync_Should_Remove_Superseded_Block_Versions()
    {
        // Arrange - Create multiple versions of the same block
        const long blockId = 1001;
        var originalPayload = new byte[] { 0x01, 0x02, 0x03 };
        var updatedPayload = new byte[] { 0x04, 0x05, 0x06, 0x07 };
        var finalPayload = new byte[] { 0x08, 0x09, 0x0A, 0x0B, 0x0C };

        // Write original block
        var originalBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = originalPayload
        };
        await _blockManager.WriteBlockAsync(originalBlock);

        // Write updated version (same ID, different payload)
        var updatedBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks + 1000,
            BlockId = blockId,
            Payload = updatedPayload
        };
        await _blockManager.WriteBlockAsync(updatedBlock);

        // Write final version
        var finalBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks + 2000,
            BlockId = blockId,
            Payload = finalPayload
        };
        await _blockManager.WriteBlockAsync(finalBlock);

        var fileSizeBeforeCompact = new FileInfo(_testFile).Length;
        _output.WriteLine($"File size before compact: {fileSizeBeforeCompact} bytes");

        // Act - Compact the file
        await _blockManager.CompactAsync();

        var fileSizeAfterCompact = new FileInfo(_testFile).Length;
        _output.WriteLine($"File size after compact: {fileSizeAfterCompact} bytes");

        // Assert
        // 1. File should be smaller after compaction
        Assert.True(fileSizeAfterCompact < fileSizeBeforeCompact, 
            $"File should be smaller after compaction. Before: {fileSizeBeforeCompact}, After: {fileSizeAfterCompact}");

        // 2. Should only have one location for the block ID
        var locations = _blockManager.GetBlockLocations();
        Assert.True(locations.ContainsKey(blockId));

        // 3. Should be able to read the latest version
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(finalPayload, readResult.Value.Payload);

        _output.WriteLine($"Space reclaimed: {fileSizeBeforeCompact - fileSizeAfterCompact} bytes");
    }

    [Fact]
    public async Task CompactAsync_Should_Preserve_Latest_Blocks_Only()
    {
        // Arrange - Create multiple blocks with multiple versions each
        var blocks = new[]
        {
            (Id: 2001L, Payloads: new[] { new byte[] { 0x01 }, new byte[] { 0x02 }, new byte[] { 0x03 } }),
            (Id: 2002L, Payloads: new[] { new byte[] { 0x04 }, new byte[] { 0x05 } }),
            (Id: 2003L, Payloads: new[] { new byte[] { 0x06 } }) // Only one version
        };

        // Write all versions of all blocks
        foreach (var (id, payloads) in blocks)
        {
            for (int version = 0; version < payloads.Length; version++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + (version * 1000),
                    BlockId = id,
                    Payload = payloads[version]
                };
                await _blockManager.WriteBlockAsync(block);
            }
        }

        var locationsBeforeCompact = _blockManager.GetBlockLocations();
        _output.WriteLine($"Locations before compact: {locationsBeforeCompact.Count}");

        // Act
        await _blockManager.CompactAsync();

        // Assert
        var locationsAfterCompact = _blockManager.GetBlockLocations();
        _output.WriteLine($"Locations after compact: {locationsAfterCompact.Count}");

        // Should have exactly one location per unique block ID
        Assert.Equal(blocks.Length, locationsAfterCompact.Count);

        // Verify each block has the latest version's payload
        foreach (var (id, payloads) in blocks)
        {
            Assert.True(locationsAfterCompact.ContainsKey(id));
            
            var readResult = await _blockManager.ReadBlockAsync(id);
            Assert.True(readResult.IsSuccess);
            
            var latestPayload = payloads[payloads.Length - 1];
            Assert.Equal(latestPayload, readResult.Value.Payload);
        }
    }

    [Fact]
    public async Task CompactAsync_Should_Maintain_Index_Consistency()
    {
        // Arrange - Create blocks with multiple versions
        const int blockCount = 50;
        const int versionsPerBlock = 3;

        for (int blockNum = 0; blockNum < blockCount; blockNum++)
        {
            for (int version = 0; version < versionsPerBlock; version++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + (version * 1000),
                    BlockId = 3000 + blockNum,
                    Payload = new byte[] { (byte)blockNum, (byte)version }
                };
                await _blockManager.WriteBlockAsync(block);
            }
        }

        // Act
        await _blockManager.CompactAsync();

        // Assert - Verify all blocks are still readable and have correct latest versions
        var locations = _blockManager.GetBlockLocations();
        Assert.Equal(blockCount, locations.Count);

        for (int blockNum = 0; blockNum < blockCount; blockNum++)
        {
            long blockId = 3000 + blockNum;
            Assert.True(locations.ContainsKey(blockId));

            var readResult = await _blockManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess);
            
            // Should have the latest version (versionsPerBlock - 1)
            var expectedPayload = new byte[] { (byte)blockNum, (byte)(versionsPerBlock - 1) };
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        _output.WriteLine($"Successfully verified {blockCount} blocks after compaction");
    }

    [Fact]
    public async Task CompactAsync_Should_Handle_Single_Block_File()
    {
        // Arrange - File with only one block
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 4001,
            Payload = new byte[] { 0xAA, 0xBB, 0xCC }
        };

        await _blockManager.WriteBlockAsync(block);
        var fileSizeBeforeCompact = new FileInfo(_testFile).Length;

        // Act
        await _blockManager.CompactAsync();

        // Assert - File size should remain the same (no duplicates to remove)
        var fileSizeAfterCompact = new FileInfo(_testFile).Length;
        Assert.Equal(fileSizeBeforeCompact, fileSizeAfterCompact);

        // Block should still be readable
        var readResult = await _blockManager.ReadBlockAsync(4001);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(block.Payload, readResult.Value.Payload);
    }

    [Fact]
    public async Task CompactAsync_Should_Handle_Empty_File()
    {
        // Arrange - Empty file (no blocks written)
        var fileSizeBeforeCompact = new FileInfo(_testFile).Length;

        // Act
        await _blockManager.CompactAsync();

        // Assert - Should complete without error
        var fileSizeAfterCompact = new FileInfo(_testFile).Length;
        Assert.Equal(fileSizeBeforeCompact, fileSizeAfterCompact);

        var locations = _blockManager.GetBlockLocations();
        Assert.Empty(locations);
    }

    [Fact]
    public async Task CompactAsync_Should_Reclaim_Significant_Space_With_Many_Overwrites()
    {
        // Arrange - Create a scenario with many overwrites of the same blocks
        const int blockCount = 10;
        const int overwriteCount = 20;

        for (int overwrite = 0; overwrite < overwriteCount; overwrite++)
        {
            for (int blockNum = 0; blockNum < blockCount; blockNum++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + (overwrite * 1000),
                    BlockId = 5000 + blockNum,
                    Payload = new byte[100] // 100 bytes per block
                };
                
                // Fill with pattern based on overwrite iteration
                for (int i = 0; i < block.Payload.Length; i++)
                {
                    block.Payload[i] = (byte)(overwrite % 256);
                }

                await _blockManager.WriteBlockAsync(block);
            }
        }

        var fileSizeBeforeCompact = new FileInfo(_testFile).Length;
        _output.WriteLine($"File size before compact (after {overwriteCount} overwrites): {fileSizeBeforeCompact:N0} bytes");

        // Act
        await _blockManager.CompactAsync();

        var fileSizeAfterCompact = new FileInfo(_testFile).Length;
        _output.WriteLine($"File size after compact: {fileSizeAfterCompact:N0} bytes");

        // Assert
        var spaceReclaimed = fileSizeBeforeCompact - fileSizeAfterCompact;
        var reclaimPercentage = (double)spaceReclaimed / fileSizeBeforeCompact * 100;

        _output.WriteLine($"Space reclaimed: {spaceReclaimed:N0} bytes ({reclaimPercentage:F1}%)");

        // Should reclaim significant space (expect >80% with 20 overwrites)
        Assert.True(reclaimPercentage > 80, 
            $"Should reclaim >80% of space with many overwrites, but only reclaimed {reclaimPercentage:F1}%");

        // Verify all latest blocks are still accessible
        var locations = _blockManager.GetBlockLocations();
        Assert.Equal(blockCount, locations.Count);

        for (int blockNum = 0; blockNum < blockCount; blockNum++)
        {
            var readResult = await _blockManager.ReadBlockAsync(5000 + blockNum);
            Assert.True(readResult.IsSuccess);
            
            // Should have payload from last overwrite (overwriteCount - 1)
            Assert.All(readResult.Value.Payload, b => Assert.Equal((byte)((overwriteCount - 1) % 256), b));
        }
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
        
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort
            }
        }
    }
}