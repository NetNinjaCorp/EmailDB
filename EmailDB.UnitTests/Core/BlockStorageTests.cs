using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Core tests for EmailDB block storage functionality.
/// These tests verify the fundamental block storage operations work correctly.
/// </summary>
public class BlockStorageTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public BlockStorageTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Write_And_Read_Block_With_All_Fields()
    {
        // Arrange
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0x42,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 12345,
            Payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }
        };

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        
        // Assert
        Assert.True(readResult.IsSuccess);
        var readBlock = readResult.Value;
        
        Assert.Equal(block.Version, readBlock.Version);
        Assert.Equal(block.Type, readBlock.Type);
        Assert.Equal(block.Flags, readBlock.Flags);
        Assert.Equal(block.Encoding, readBlock.Encoding);
        Assert.Equal(block.Timestamp, readBlock.Timestamp);
        Assert.Equal(block.BlockId, readBlock.BlockId);
        Assert.Equal(block.Payload, readBlock.Payload);
        
        _output.WriteLine($"Successfully wrote and read block {block.BlockId}");
    }

    [Fact]
    public async Task Should_Handle_Multiple_Block_Writes()
    {
        // Arrange
        const int blockCount = 10;
        
        // Act - Write blocks
        for (int i = 0; i < blockCount; i++)
        {
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 1000 + i,
                Payload = BitConverter.GetBytes(i)
            };

            var result = await _blockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
        }

        // Assert - Read all blocks back
        for (int i = 0; i < blockCount; i++)
        {
            var result = await _blockManager.ReadBlockAsync(1000 + i);
            Assert.True(result.IsSuccess);
            Assert.Equal(BitConverter.GetBytes(i), result.Value.Payload);
        }
        
        _output.WriteLine($"Successfully wrote and read {blockCount} blocks");
    }

    [Fact]
    public async Task Should_Return_Error_For_NonExistent_Block()
    {
        // Act
        var result = await _blockManager.ReadBlockAsync(99999);
        
        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_Track_Block_Locations()
    {
        // Arrange & Act
        var blockIds = new[] { 2001L, 2002L, 2003L };
        
        foreach (var id in blockIds)
        {
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = id,
                Payload = new byte[100]
            };

            await _blockManager.WriteBlockAsync(block);
        }

        // Assert
        var locations = _blockManager.GetBlockLocations();
        
        foreach (var id in blockIds)
        {
            Assert.True(locations.ContainsKey(id));
            Assert.True(locations[id].Position >= 0);
            Assert.True(locations[id].Length > 0);
        }
        
        _output.WriteLine($"Block location index contains {locations.Count} entries");
    }

    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.Cleanup)]
    [InlineData(BlockType.ZoneTreeSegment_KV)]
    [InlineData(BlockType.ZoneTreeSegment_Vector)]
    [InlineData(BlockType.FreeSpace)]
    public async Task Should_Support_All_Block_Types(BlockType blockType)
    {
        // Arrange
        var block = new Block
        {
            Version = 1,
            Type = blockType,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 3000 + (int)blockType,
            Payload = new byte[] { 0xAA, 0xBB, 0xCC }
        };

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);

        // Assert
        Assert.True(writeResult.IsSuccess);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(blockType, readResult.Value.Type);
        
        _output.WriteLine($"Block type {blockType} supported");
    }

    [Theory]
    [InlineData(PayloadEncoding.Protobuf)]
    [InlineData(PayloadEncoding.CapnProto)]
    [InlineData(PayloadEncoding.Json)]
    [InlineData(PayloadEncoding.RawBytes)]
    public async Task Should_Preserve_Payload_Encoding(PayloadEncoding encoding)
    {
        // Arrange
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = encoding,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 4000 + (int)encoding,
            Payload = new byte[] { 0x01, 0x02, 0x03, 0x04 }
        };

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);

        // Assert
        Assert.True(writeResult.IsSuccess);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(encoding, readResult.Value.Encoding);
        
        _output.WriteLine($"Payload encoding {encoding} preserved");
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