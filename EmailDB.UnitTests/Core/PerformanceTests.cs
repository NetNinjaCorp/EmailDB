using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Performance tests for EmailDB to ensure it meets speed requirements.
/// These tests verify that EmailDB is fast and efficient.
/// </summary>
public class PerformanceTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Write_1000_Blocks_Under_1_Second()
    {
        // Arrange
        const int blockCount = 1000;
        var random = new Random(42);
        var stopwatch = Stopwatch.StartNew();

        // Act
        for (int i = 0; i < blockCount; i++)
        {
            var data = new byte[256];
            random.NextBytes(data);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 10000 + i,
                Payload = data
            };

            var result = await _blockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
        }

        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, 
            $"Writing {blockCount} blocks took {stopwatch.ElapsedMilliseconds}ms, should be under 1000ms");
        
        _output.WriteLine($"Wrote {blockCount} blocks in {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {stopwatch.ElapsedMilliseconds / (double)blockCount:F3}ms per block");
    }

    [Fact]
    public async Task Should_Read_Random_Blocks_Quickly()
    {
        // Arrange - Write blocks first
        const int blockCount = 1000;
        var random = new Random(123);
        var blockIds = new List<long>();
        
        for (int i = 0; i < blockCount; i++)
        {
            var data = new byte[512];
            random.NextBytes(data);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 20000 + i,
                Payload = data
            };

            await _blockManager.WriteBlockAsync(block);
            blockIds.Add(block.BlockId);
        }

        // Act - Read 100 random blocks
        const int readCount = 100;
        var readTimes = new List<double>();
        
        for (int i = 0; i < readCount; i++)
        {
            var targetId = blockIds[random.Next(blockIds.Count)];
            
            var stopwatch = Stopwatch.StartNew();
            var result = await _blockManager.ReadBlockAsync(targetId);
            stopwatch.Stop();
            
            Assert.True(result.IsSuccess);
            readTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        // Assert
        var avgReadTime = readTimes.Count > 0 ? readTimes.Average() : 0;
        Assert.True(avgReadTime < 10, 
            $"Average read time {avgReadTime:F3}ms should be under 10ms");
        
        _output.WriteLine($"Random read performance:");
        _output.WriteLine($"- Average: {avgReadTime:F3}ms");
        _output.WriteLine($"- Min: {readTimes.Min():F3}ms");
        _output.WriteLine($"- Max: {readTimes.Max():F3}ms");
    }

    [Fact]
    public async Task Should_Handle_Large_Blocks_Efficiently()
    {
        // Arrange
        var sizes = new[] { 1024, 10240, 102400, 1048576 }; // 1KB, 10KB, 100KB, 1MB
        var random = new Random(456);

        // Act & Assert
        foreach (var size in sizes)
        {
            var data = new byte[size];
            random.NextBytes(data);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 30000 + size,
                Payload = data
            };

            var writeStopwatch = Stopwatch.StartNew();
            var writeResult = await _blockManager.WriteBlockAsync(block);
            writeStopwatch.Stop();
            
            Assert.True(writeResult.IsSuccess);

            var readStopwatch = Stopwatch.StartNew();
            var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
            readStopwatch.Stop();
            
            Assert.True(readResult.IsSuccess);
            Assert.Equal(data.Length, readResult.Value.Payload.Length);
            
            _output.WriteLine($"{size / 1024}KB block: Write={writeStopwatch.ElapsedMilliseconds}ms, Read={readStopwatch.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public async Task Should_Build_Index_Quickly_For_Large_Files()
    {
        // Arrange - Create a file with many blocks
        const int blockCount = 5000;
        var random = new Random(789);
        
        for (int i = 0; i < blockCount; i++)
        {
            var data = new byte[100 + random.Next(400)]; // 100-500 bytes
            random.NextBytes(data);
            
            var block = new Block
            {
                Version = 1,
                Type = (BlockType)(i % 5 + 1),
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 40000 + i,
                Payload = data
            };

            await _blockManager.WriteBlockAsync(block);
        }

        // Act - Time index retrieval
        var stopwatch = Stopwatch.StartNew();
        var locations = _blockManager.GetBlockLocations();
        stopwatch.Stop();

        // Assert
        Assert.Equal(blockCount, locations.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 100, 
            $"Getting block locations took {stopwatch.ElapsedMilliseconds}ms, should be under 100ms");
        
        _output.WriteLine($"Retrieved {locations.Count} block locations in {stopwatch.ElapsedMilliseconds}ms");
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
        
        if (File.Exists(_testFile))
        {
            try
            {
                var fileSize = new FileInfo(_testFile).Length;
                _output.WriteLine($"Test file size: {fileSize:N0} bytes ({fileSize / 1024.0 / 1024.0:F2} MB)");
                File.Delete(_testFile);
            }
            catch
            {
                // Best effort
            }
        }
    }
}