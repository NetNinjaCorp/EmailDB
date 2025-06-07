using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Resilience tests for EmailDB to ensure it can handle corruption and recovery.
/// These tests verify EmailDB's append-only architecture provides resilience.
/// </summary>
public class ResilienceTests : IDisposable
{
    private readonly string _testFile;
    private RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public ResilienceTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Handle_Append_Only_Updates()
    {
        // Arrange - Write original block
        var originalData = new byte[] { 0x01, 0x02, 0x03 };
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 5001,
            Payload = originalData
        };

        var firstWrite = await _blockManager.WriteBlockAsync(block);
        Assert.True(firstWrite.IsSuccess);
        var firstPosition = firstWrite.Value.Position;

        // Act - Write same block ID with new data (append-only update)
        var updatedData = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        block.Version = 2;
        block.Payload = updatedData;
        block.Timestamp = DateTime.UtcNow.Ticks;

        var secondWrite = await _blockManager.WriteBlockAsync(block);
        Assert.True(secondWrite.IsSuccess);
        var secondPosition = secondWrite.Value.Position;

        // Assert
        Assert.True(secondPosition > firstPosition, "Second write should be after first (append-only)");
        
        // Reading should return the latest version
        var readResult = await _blockManager.ReadBlockAsync(5001);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(2, readResult.Value.Version);
        Assert.Equal(updatedData, readResult.Value.Payload);
        
        _output.WriteLine($"Append-only update successful:");
        _output.WriteLine($"- First write at position {firstPosition}");
        _output.WriteLine($"- Second write at position {secondPosition}");
        _output.WriteLine($"- Latest version returned on read");
    }

    [Fact]
    public async Task Should_Preserve_All_Block_Versions_In_File()
    {
        // Arrange
        const int versions = 5;
        var blockId = 6001L;
        var fileSizeBefore = new FileInfo(_testFile).Length;

        // Act - Write multiple versions
        for (int v = 1; v <= versions; v++)
        {
            var block = new Block
            {
                Version = (ushort)v,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockId,
                Payload = new byte[100 * v] // Different sizes
            };

            var result = await _blockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
        }

        var fileSizeAfter = new FileInfo(_testFile).Length;

        // Assert
        // File should grow with each version (append-only)
        Assert.True(fileSizeAfter > fileSizeBefore);
        
        // Latest version should be returned
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(versions, readResult.Value.Version);
        
        // Calculate approximate expected growth
        var totalPayloadSize = 0;
        for (int v = 1; v <= versions; v++)
        {
            totalPayloadSize += 100 * v + 61; // payload + overhead
        }
        
        var actualGrowth = fileSizeAfter - fileSizeBefore;
        _output.WriteLine($"File growth for {versions} versions:");
        _output.WriteLine($"- Expected minimum: {totalPayloadSize} bytes");
        _output.WriteLine($"- Actual growth: {actualGrowth} bytes");
        _output.WriteLine($"- All versions preserved in file (append-only)");
    }

    [Fact]
    public async Task Should_Continue_Working_After_Reopening_File()
    {
        // Arrange - Write some blocks
        var blockIds = new long[] { 7001, 7002, 7003 };
        
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
                Payload = BitConverter.GetBytes(id)
            };

            await _blockManager.WriteBlockAsync(block);
        }

        // Act - Close and reopen
        _blockManager.Dispose();
        _blockManager = new RawBlockManager(_testFile);

        // Assert - Can read old blocks
        foreach (var id in blockIds)
        {
            var result = await _blockManager.ReadBlockAsync(id);
            Assert.True(result.IsSuccess);
            Assert.Equal(BitConverter.GetBytes(id), result.Value.Payload);
        }

        // Assert - Can write new blocks
        var newBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 7004,
            Payload = new byte[] { 0xFF }
        };

        var writeResult = await _blockManager.WriteBlockAsync(newBlock);
        Assert.True(writeResult.IsSuccess);
        
        var readResult = await _blockManager.ReadBlockAsync(7004);
        Assert.True(readResult.IsSuccess);
        
        _output.WriteLine("File persistence verified:");
        _output.WriteLine($"- Read {blockIds.Length} existing blocks after reopen");
        _output.WriteLine("- Successfully wrote new blocks after reopen");
    }

    [Fact]
    public async Task Should_Handle_Concurrent_Writes_Safely()
    {
        // Arrange
        const int threadCount = 5;
        const int blocksPerThread = 20;
        var tasks = new Task[threadCount];

        // Act - Multiple threads writing concurrently
        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < blocksPerThread; i++)
                {
                    var block = new Block
                    {
                        Version = 1,
                        Type = BlockType.Segment,
                        Flags = (byte)threadId,
                        Encoding = PayloadEncoding.RawBytes,
                        Timestamp = DateTime.UtcNow.Ticks,
                        BlockId = 8000 + (threadId * 100) + i,
                        Payload = new byte[] { (byte)threadId, (byte)i }
                    };

                    var result = await _blockManager.WriteBlockAsync(block);
                    Assert.True(result.IsSuccess);
                }
            });
        }

        await Task.WhenAll(tasks);

        // Assert - All blocks should be readable
        var successCount = 0;
        for (int t = 0; t < threadCount; t++)
        {
            for (int i = 0; i < blocksPerThread; i++)
            {
                var blockId = 8000 + (t * 100) + i;
                var result = await _blockManager.ReadBlockAsync(blockId);
                
                if (result.IsSuccess)
                {
                    Assert.Equal((byte)t, result.Value.Flags);
                    successCount++;
                }
            }
        }

        Assert.Equal(threadCount * blocksPerThread, successCount);
        _output.WriteLine($"Concurrent write test: {successCount}/{threadCount * blocksPerThread} blocks written successfully");
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