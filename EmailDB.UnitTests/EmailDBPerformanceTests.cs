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
using EmailDB.Format.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Performance and stress tests for EmailDB operations
/// </summary>
public class EmailDBPerformanceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testFile;
    private readonly RawBlockManager _rawBlockManager;
    private readonly ITestOutputHelper _output;

    public EmailDBPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _testDirectory = Path.Combine(Path.GetTempPath(), $"EmailDBPerfTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _testFile = Path.Combine(_testDirectory, "perf.emdb");
        _rawBlockManager = new RawBlockManager(_testFile);
    }

    #region Write Performance Tests

    [Fact]
    public async Task Should_Write_1000_Blocks_Under_5_Seconds()
    {
        // Arrange
        var blockCount = 1000;
        var blocks = new List<Block>();
        
        for (int i = 0; i < blockCount; i++)
        {
            blocks.Add(new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 20000 + i,
                Payload = Encoding.UTF8.GetBytes($"Performance test block {i} with some additional data to make it realistic")
            });
        }

        // Act
        var sw = Stopwatch.StartNew();
        foreach (var block in blocks)
        {
            var result = await _rawBlockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
        }
        sw.Stop();

        // Assert
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Writing {blockCount} blocks took {sw.ElapsedMilliseconds}ms");
        
        _output.WriteLine($"Write performance: {blockCount} blocks in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {sw.ElapsedMilliseconds / (double)blockCount:F2}ms per block");
        _output.WriteLine($"Throughput: {blockCount / (sw.ElapsedMilliseconds / 1000.0):F2} blocks/second");
    }

    [Fact]
    public async Task Should_Handle_Large_Block_Writes_Efficiently()
    {
        // Test with various block sizes
        var testCases = new[]
        {
            (size: 1024, count: 100, name: "1KB"),        // 100 x 1KB blocks
            (size: 10240, count: 50, name: "10KB"),       // 50 x 10KB blocks
            (size: 102400, count: 20, name: "100KB"),     // 20 x 100KB blocks
            (size: 1048576, count: 5, name: "1MB")        // 5 x 1MB blocks
        };

        foreach (var testCase in testCases)
        {
            var sw = Stopwatch.StartNew();
            var totalBytes = 0L;

            for (int i = 0; i < testCase.count; i++)
            {
                var payload = new byte[testCase.size];
                new Random(42).NextBytes(payload);
                
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = 30000 + (testCase.size * 10) + i,
                    Payload = payload
                };

                var result = await _rawBlockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess);
                totalBytes += payload.Length;
            }
            
            sw.Stop();
            
            var throughputMBps = (totalBytes / 1048576.0) / (sw.ElapsedMilliseconds / 1000.0);
            _output.WriteLine($"{testCase.name} blocks: {testCase.count} blocks in {sw.ElapsedMilliseconds}ms");
            _output.WriteLine($"  Throughput: {throughputMBps:F2} MB/s");
        }
    }

    #endregion

    #region Read Performance Tests

    [Fact]
    public async Task Should_Read_1000_Blocks_Under_3_Seconds()
    {
        // Arrange - Write blocks first
        var blockCount = 1000;
        var blockIds = new List<long>();
        
        for (int i = 0; i < blockCount; i++)
        {
            var blockId = 40000 + i;
            blockIds.Add(blockId);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockId,
                Payload = Encoding.UTF8.GetBytes($"Read performance test block {i}")
            };
            
            await _rawBlockManager.WriteBlockAsync(block);
        }

        // Act - Read all blocks
        var sw = Stopwatch.StartNew();
        foreach (var blockId in blockIds)
        {
            var result = await _rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(result.IsSuccess);
        }
        sw.Stop();

        // Assert
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Reading {blockCount} blocks took {sw.ElapsedMilliseconds}ms");
        
        _output.WriteLine($"Read performance: {blockCount} blocks in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {sw.ElapsedMilliseconds / (double)blockCount:F2}ms per block");
        _output.WriteLine($"Throughput: {blockCount / (sw.ElapsedMilliseconds / 1000.0):F2} blocks/second");
    }

    [Fact]
    public async Task Should_Handle_Random_Access_Reads_Efficiently()
    {
        // Arrange - Write blocks in sequential order
        var blockCount = 500;
        var blockIds = new List<long>();
        
        for (int i = 0; i < blockCount; i++)
        {
            var blockId = 50000 + i;
            blockIds.Add(blockId);
            
            await _rawBlockManager.WriteBlockAsync(new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockId,
                Payload = Encoding.UTF8.GetBytes($"Random access block {i}")
            });
        }

        // Act - Read blocks in random order
        var random = new Random(42);
        var randomizedIds = blockIds.OrderBy(x => random.Next()).ToList();
        
        var sw = Stopwatch.StartNew();
        foreach (var blockId in randomizedIds)
        {
            var result = await _rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(result.IsSuccess);
        }
        sw.Stop();

        _output.WriteLine($"Random access read: {blockCount} blocks in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average: {sw.ElapsedMilliseconds / (double)blockCount:F2}ms per random read");
    }

    #endregion

    #region Concurrent Operations Tests

    [Fact]
    public async Task Should_Handle_Concurrent_Writes_Efficiently()
    {
        // Arrange
        var concurrency = 10;
        var blocksPerThread = 100;
        var totalBlocks = concurrency * blocksPerThread;
        
        // Act
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task>();
        
        for (int thread = 0; thread < concurrency; thread++)
        {
            var threadId = thread;
            var task = Task.Run(async () =>
            {
                for (int i = 0; i < blocksPerThread; i++)
                {
                    var block = new Block
                    {
                        Version = 1,
                        Type = BlockType.Segment,
                        Flags = 0,
                        Encoding = PayloadEncoding.RawBytes,
                        Timestamp = DateTime.UtcNow.Ticks,
                        BlockId = 60000 + (threadId * 1000) + i,
                        Payload = Encoding.UTF8.GetBytes($"Concurrent write thread {threadId} block {i}")
                    };
                    
                    var result = await _rawBlockManager.WriteBlockAsync(block);
                    Assert.True(result.IsSuccess);
                }
            });
            
            tasks.Add(task);
        }
        
        await Task.WhenAll(tasks);
        sw.Stop();

        _output.WriteLine($"Concurrent write: {totalBlocks} blocks with {concurrency} threads in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Throughput: {totalBlocks / (sw.ElapsedMilliseconds / 1000.0):F2} blocks/second");
    }

    [Fact]
    public async Task Should_Handle_Mixed_Read_Write_Operations()
    {
        // Arrange - Pre-write some blocks
        var existingBlockIds = new List<long>();
        for (int i = 0; i < 200; i++)
        {
            var blockId = 70000 + i;
            existingBlockIds.Add(blockId);
            
            await _rawBlockManager.WriteBlockAsync(new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockId,
                Payload = Encoding.UTF8.GetBytes($"Pre-existing block {i}")
            });
        }

        // Act - Mixed operations
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task>();
        
        // Writer tasks
        for (int i = 0; i < 5; i++)
        {
            var writerId = i;
            tasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var block = new Block
                    {
                        Version = 1,
                        Type = BlockType.Segment,
                        Flags = 0,
                        Encoding = PayloadEncoding.RawBytes,
                        Timestamp = DateTime.UtcNow.Ticks,
                        BlockId = 80000 + (writerId * 100) + j,
                        Payload = Encoding.UTF8.GetBytes($"New block from writer {writerId}")
                    };
                    
                    await _rawBlockManager.WriteBlockAsync(block);
                }
            }));
        }
        
        // Reader tasks
        for (int i = 0; i < 5; i++)
        {
            var readerId = i;
            tasks.Add(Task.Run(async () =>
            {
                var random = new Random(readerId);
                for (int j = 0; j < 100; j++)
                {
                    var blockId = existingBlockIds[random.Next(existingBlockIds.Count)];
                    await _rawBlockManager.ReadBlockAsync(blockId);
                }
            }));
        }
        
        await Task.WhenAll(tasks);
        sw.Stop();

        _output.WriteLine($"Mixed operations completed in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"5 writers (250 writes) + 5 readers (500 reads) = 750 operations");
        _output.WriteLine($"Throughput: {750 / (sw.ElapsedMilliseconds / 1000.0):F2} operations/second");
    }

    #endregion

    #region Memory and Resource Tests

    [Fact]
    public async Task Should_Handle_Memory_Efficiently_With_Large_Dataset()
    {
        // Arrange
        var initialMemory = GC.GetTotalMemory(true);
        var blockCount = 5000;
        var blockSize = 1024; // 1KB per block
        
        // Act - Write many blocks
        for (int i = 0; i < blockCount; i++)
        {
            var payload = new byte[blockSize];
            new Random(i).NextBytes(payload);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 90000 + i,
                Payload = payload
            };
            
            await _rawBlockManager.WriteBlockAsync(block);
            
            // Force GC every 1000 blocks to check memory usage
            if (i % 1000 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
        
        var finalMemory = GC.GetTotalMemory(true);
        var memoryUsedMB = (finalMemory - initialMemory) / 1048576.0;
        
        _output.WriteLine($"Memory usage after writing {blockCount} blocks: {memoryUsedMB:F2} MB");
        _output.WriteLine($"File size: {new FileInfo(_testFile).Length / 1048576.0:F2} MB");
        
        // Memory usage should be reasonable (not storing all blocks in memory)
        Assert.True(memoryUsedMB < 100, $"Memory usage too high: {memoryUsedMB:F2} MB");
    }

    #endregion

    #region File Size and Indexing Tests

    [Fact]
    public async Task Should_Build_Index_Efficiently_For_Large_File()
    {
        // Arrange - Create a file with many blocks
        var blockCount = 2000;
        
        for (int i = 0; i < blockCount; i++)
        {
            await _rawBlockManager.WriteBlockAsync(new Block
            {
                Version = 1,
                Type = i % 5 == 0 ? BlockType.Metadata : BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 100000 + i,
                Payload = Encoding.UTF8.GetBytes($"Index test block {i}")
            });
        }
        
        _rawBlockManager.Dispose();

        // Act - Reopen and measure indexing time
        var sw = Stopwatch.StartNew();
        using var newManager = new RawBlockManager(_testFile, createIfNotExists: false);
        sw.Stop();
        
        var allLocations = newManager.GetAllBlockLocations();
        
        // Assert
        Assert.Equal(blockCount, allLocations.Count);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Indexing {blockCount} blocks took {sw.ElapsedMilliseconds}ms");
        
        _output.WriteLine($"Index rebuild for {blockCount} blocks: {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"File size: {new FileInfo(_testFile).Length / 1048576.0:F2} MB");
    }

    #endregion

    #region Stress Tests

    [Theory]
    [InlineData(100, 10240)]    // 100 blocks of 10KB
    [InlineData(1000, 1024)]    // 1000 blocks of 1KB
    [InlineData(10, 1048576)]   // 10 blocks of 1MB
    public async Task Should_Handle_Various_Workloads(int blockCount, int blockSize)
    {
        // Arrange
        var blocks = new List<Block>();
        var totalBytes = 0L;
        
        for (int i = 0; i < blockCount; i++)
        {
            var payload = new byte[blockSize];
            new Random(i).NextBytes(payload);
            totalBytes += payload.Length;
            
            blocks.Add(new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 200000 + i,
                Payload = payload
            });
        }

        // Act - Write
        var writeStopwatch = Stopwatch.StartNew();
        foreach (var block in blocks)
        {
            var result = await _rawBlockManager.WriteBlockAsync(block);
            Assert.True(result.IsSuccess);
        }
        writeStopwatch.Stop();

        // Act - Read
        var readStopwatch = Stopwatch.StartNew();
        foreach (var block in blocks)
        {
            var result = await _rawBlockManager.ReadBlockAsync(block.BlockId);
            Assert.True(result.IsSuccess);
            Assert.Equal(block.Payload.Length, result.Value.Payload.Length);
        }
        readStopwatch.Stop();

        // Report
        var totalMB = totalBytes / 1048576.0;
        var writeThroughput = totalMB / (writeStopwatch.ElapsedMilliseconds / 1000.0);
        var readThroughput = totalMB / (readStopwatch.ElapsedMilliseconds / 1000.0);
        
        _output.WriteLine($"Workload: {blockCount} blocks × {blockSize / 1024}KB = {totalMB:F2}MB");
        _output.WriteLine($"Write: {writeStopwatch.ElapsedMilliseconds}ms ({writeThroughput:F2} MB/s)");
        _output.WriteLine($"Read: {readStopwatch.ElapsedMilliseconds}ms ({readThroughput:F2} MB/s)");
    }

    #endregion

    public void Dispose()
    {
        _rawBlockManager?.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}