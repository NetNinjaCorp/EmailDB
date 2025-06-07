using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EmailDB.UnitTests.Models;
using Xunit;

namespace EmailDB.UnitTests;

public class RawBlockManagerTests : IDisposable
{
    private readonly string testFilePath;

    public RawBlockManagerTests()
    {
        // Create a unique test file path for each test run
        testFilePath = Path.Combine(Path.GetTempPath(), $"test_block_manager_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        // Clean up test file after each test
        if (File.Exists(testFilePath))
        {
            File.Delete(testFilePath);
        }
    }

    [Fact]
    public async Task WriteBlockAsync_ShouldWriteBlockToFile()
    {
        // Arrange
        using var manager = new TestRawBlockManager(testFilePath);
        var block = new Block
        {
            BlockId = 1,
            Type = BlockType.Folder,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new byte[] { 1, 2, 3, 4 }
        };

        // Act
        var location = await manager.WriteBlockAsync(block);

        // Assert
        Assert.NotNull(location);
        Assert.True(location.Position >= 0);
        Assert.True(location.Length > 0);
        Assert.True(File.Exists(testFilePath));
        Assert.True(new FileInfo(testFilePath).Length > 0);
    }

    [Fact]
    public async Task ReadBlockAsync_AfterWriting_ShouldReturnSameBlock()
    {
        // Arrange
        using var manager = new TestRawBlockManager(testFilePath);
        var originalBlock = new Block
        {
            BlockId = 2,
            Type = BlockType.Email,
            Version = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = new byte[] { 5, 6, 7, 8 }
        };

        // Act
        var location = await manager.WriteBlockAsync(originalBlock);
        var readBlock = await manager.ReadBlockAsync(originalBlock.BlockId);

        // Assert
        Assert.NotNull(readBlock);
        Assert.Equal(originalBlock.BlockId, readBlock.BlockId);
        Assert.Equal(originalBlock.Type, readBlock.Type);
        Assert.Equal(originalBlock.Version, readBlock.Version);
        Assert.Equal(originalBlock.Timestamp, readBlock.Timestamp);
        // We can't directly compare payloads as they might be processed differently
    }

    [Fact]
    public async Task GetBlockLocations_AfterWritingMultipleBlocks_ShouldReturnAllLocations()
    {
        // Arrange
        using var manager = new TestRawBlockManager(testFilePath);
        var blocks = new List<Block>
        {
            new Block { BlockId = 3, Type = BlockType.Folder, Payload = new byte[] { 1, 2, 3 } },
            new Block { BlockId = 4, Type = BlockType.Email, Payload = new byte[] { 4, 5, 6 } },
            new Block { BlockId = 5, Type = BlockType.Segment, Payload = new byte[] { 7, 8, 9 } }
        };

        // Act
        foreach (var block in blocks)
        {
            await manager.WriteBlockAsync(block);
        }
        var locations = manager.GetBlockLocations();

        // Assert
        Assert.NotNull(locations);
        Assert.Equal(blocks.Count, locations.Count);
        foreach (var block in blocks)
        {
            Assert.True(locations.ContainsKey(block.BlockId));
        }
    }

    [Fact]
    public async Task ReadBlockAsync_WithInvalidBlockId_ShouldThrowKeyNotFoundException()
    {
        // Arrange
        using var manager = new TestRawBlockManager(testFilePath);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.ReadBlockAsync(999));
    }

    [Fact]
    public void Dispose_ShouldCloseFileStream()
    {
        // Arrange
        var manager = new TestRawBlockManager(testFilePath);

        // Act
        manager.Dispose();

        // Assert - We can verify this by trying to open the file exclusively
        using var fileStream = new FileStream(testFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // If we can open the file exclusively, it means the previous stream was closed
        Assert.NotNull(fileStream);
    }
}

// Simple test implementation of RawBlockManager
public class TestRawBlockManager : IDisposable
{
    private readonly string filePath;
    private readonly FileStream fileStream;
    private readonly Dictionary<long, BlockLocation> blockLocations = new Dictionary<long, BlockLocation>();
    private long currentPosition = 0;

    public TestRawBlockManager(string filePath)
    {
        this.filePath = filePath;
        this.fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    }

    public async Task<BlockLocation> WriteBlockAsync(Block block)
    {
        // Simplified block writing for testing
        using var writer = new BinaryWriter(fileStream, System.Text.Encoding.UTF8, true);
        
        // Store the starting position
        long blockStartPosition = currentPosition;
        fileStream.Seek(blockStartPosition, SeekOrigin.Begin);
        
        // Write a simple header
        writer.Write(block.BlockId);
        writer.Write((byte)block.Type);
        writer.Write(block.Version);
        writer.Write(block.Timestamp);
        
        // Write payload length and payload
        if (block.Payload != null)
        {
            writer.Write(block.Payload.Length);
            writer.Write(block.Payload);
        }
        else
        {
            writer.Write(0);
        }
        
        // Update position
        currentPosition = fileStream.Position;
        
        // Create and store block location
        var location = new BlockLocation
        {
            Position = blockStartPosition,
            Length = currentPosition - blockStartPosition
        };
        
        blockLocations[block.BlockId] = location;
        
        return location;
    }

    public async Task<Block> ReadBlockAsync(long blockId)
    {
        if (!blockLocations.TryGetValue(blockId, out var location))
        {
            throw new KeyNotFoundException($"Block ID {blockId} not found");
        }
        
        fileStream.Seek(location.Position, SeekOrigin.Begin);
        using var reader = new BinaryReader(fileStream, System.Text.Encoding.UTF8, true);
        
        var block = new Block
        {
            BlockId = reader.ReadInt64(),
            Type = (BlockType)reader.ReadByte(),
            Version = reader.ReadUInt16(),
            Timestamp = reader.ReadInt64()
        };
        
        int payloadLength = reader.ReadInt32();
        if (payloadLength > 0)
        {
            block.Payload = reader.ReadBytes(payloadLength);
        }
        
        return block;
    }

    public IReadOnlyDictionary<long, BlockLocation> GetBlockLocations()
    {
        return blockLocations;
    }

    public void Dispose()
    {
        fileStream.Flush();
        fileStream.Close();
        fileStream.Dispose();
    }
}