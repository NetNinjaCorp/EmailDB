using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Stress tests for EmailDB with very large files (>1GB).
/// Verifies performance and reliability under extreme load conditions.
/// Note: These tests are resource-intensive and may take significant time.
/// </summary>
public class LargeFileStressTests : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public LargeFileStressTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.Combine(Path.GetTempPath(), $"emaildb_stress_test_{Guid.NewGuid()}.dat");
    }

    [Fact]
    [Trait("Category", "Stress")]
    [Trait("Size", "Large")]
    public async Task Should_Handle_Large_File_With_Many_Small_Blocks()
    {
        // Arrange - Create many small blocks to reach >1GB
        const int targetBlocks = 500_000; // Should create ~1.5GB file with small blocks
        const int blockSize = 64; // Small blocks
        
        var stopwatch = Stopwatch.StartNew();
        var progressInterval = targetBlocks / 20; // Report progress every 5%

        // Act
        using (var blockManager = new RawBlockManager(_testFile))
        {
            for (int i = 0; i < targetBlocks; i++)
            {
                var payload = new byte[blockSize];
                // Fill with pattern for verification
                for (int j = 0; j < blockSize; j++)
                {
                    payload[j] = (byte)((i + j) % 256);
                }

                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + i,
                    BlockId = 100_000 + i,
                    Payload = payload
                };

                var result = await blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess, $"Failed to write block {i}");

                if (i % progressInterval == 0)
                {
                    var currentSize = new FileInfo(_testFile).Length;
                    _output.WriteLine($"Progress: {i:N0}/{targetBlocks:N0} blocks, File size: {currentSize / 1048576.0:F1} MB, Elapsed: {stopwatch.Elapsed.TotalSeconds:F1}s");
                }
            }

            stopwatch.Stop();
            var finalSize = new FileInfo(_testFile).Length;
            _output.WriteLine($"Created {targetBlocks:N0} blocks in {stopwatch.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"Final file size: {finalSize / 1048576.0:F1} MB ({finalSize / 1073741824.0:F2} GB)");
            _output.WriteLine($"Write rate: {targetBlocks / stopwatch.Elapsed.TotalSeconds:F0} blocks/sec");

            // Assert - Verify file is actually large
            Assert.True(finalSize > 1073741824, $"File should be >1GB, but is {finalSize / 1073741824.0:F2} GB");

            // Verify index performance
            var indexStopwatch = Stopwatch.StartNew();
            var locations = blockManager.GetBlockLocations();
            indexStopwatch.Stop();

            Assert.Equal(targetBlocks, locations.Count);
            _output.WriteLine($"Index lookup took: {indexStopwatch.ElapsedMilliseconds} ms for {targetBlocks:N0} blocks");

            // Test random access performance
            var random = new Random(42);
            var readStopwatch = Stopwatch.StartNew();
            const int randomReads = 1000;

            for (int i = 0; i < randomReads; i++)
            {
                var randomBlockId = 100_000 + random.Next(targetBlocks);
                var readResult = await blockManager.ReadBlockAsync(randomBlockId);
                Assert.True(readResult.IsSuccess, $"Failed to read random block {randomBlockId}");
            }

            readStopwatch.Stop();
            _output.WriteLine($"Random reads: {randomReads} reads in {readStopwatch.ElapsedMilliseconds} ms ({randomReads * 1000.0 / readStopwatch.ElapsedMilliseconds:F0} reads/sec)");
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    [Trait("Size", "Large")]
    public async Task Should_Handle_Large_File_With_Few_Large_Blocks()
    {
        // Arrange - Create fewer, larger blocks to reach >1GB
        const int targetBlocks = 1_100; // Should create >1GB with 1MB blocks
        const int blockSize = 1024 * 1024; // 1MB blocks
        
        var stopwatch = Stopwatch.StartNew();

        // Act
        using (var blockManager = new RawBlockManager(_testFile))
        {
            for (int i = 0; i < targetBlocks; i++)
            {
                var payload = new byte[blockSize];
                // Fill with repeating pattern for verification and some compressibility
                var pattern = BitConverter.GetBytes(i);
                for (int j = 0; j < blockSize; j += pattern.Length)
                {
                    var copyLength = Math.Min(pattern.Length, blockSize - j);
                    Array.Copy(pattern, 0, payload, j, copyLength);
                }

                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + i,
                    BlockId = 200_000 + i,
                    Payload = payload
                };

                var result = await blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess, $"Failed to write large block {i}");

                if (i % 100 == 0)
                {
                    var currentSize = new FileInfo(_testFile).Length;
                    _output.WriteLine($"Progress: {i}/{targetBlocks} large blocks, File size: {currentSize / 1048576.0:F1} MB");
                }
            }

            stopwatch.Stop();
            var finalSize = new FileInfo(_testFile).Length;
            _output.WriteLine($"Created {targetBlocks} large blocks in {stopwatch.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"Final file size: {finalSize / 1048576.0:F1} MB ({finalSize / 1073741824.0:F2} GB)");
            _output.WriteLine($"Write throughput: {finalSize / 1048576.0 / stopwatch.Elapsed.TotalSeconds:F1} MB/sec");

            // Assert - Verify file is large
            Assert.True(finalSize > 1073741824, $"File should be >1GB, but is {finalSize / 1073741824.0:F2} GB");

            // Test reading large blocks
            var readStopwatch = Stopwatch.StartNew();
            var testBlockId = 200_000 + (targetBlocks / 2); // Read middle block
            
            var readResult = await blockManager.ReadBlockAsync(testBlockId);
            readStopwatch.Stop();
            
            Assert.True(readResult.IsSuccess);
            Assert.Equal(blockSize, readResult.Value.Payload.Length);
            _output.WriteLine($"Large block read took: {readStopwatch.ElapsedMilliseconds} ms for {blockSize / 1048576.0:F1} MB");
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    [Trait("Size", "Large")]
    public async Task Should_Handle_Large_File_Reindexing_Performance()
    {
        // Arrange - Create a large file first
        const int blockCount = 100_000;
        const int blockSize = 256;

        using (var blockManager = new RawBlockManager(_testFile))
        {
            for (int i = 0; i < blockCount; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + i,
                    BlockId = 300_000 + i,
                    Payload = new byte[blockSize]
                };

                // Fill payload with pattern
                for (int j = 0; j < blockSize; j++)
                {
                    block.Payload[j] = (byte)((i + j) % 256);
                }

                await blockManager.WriteBlockAsync(block);

                if (i % 10000 == 0)
                {
                    _output.WriteLine($"Setup progress: {i:N0}/{blockCount:N0} blocks");
                }
            }
        }

        var fileSize = new FileInfo(_testFile).Length;
        _output.WriteLine($"Test file created: {fileSize / 1048576.0:F1} MB with {blockCount:N0} blocks");

        // Act - Test reindexing performance
        var reindexStopwatch = Stopwatch.StartNew();
        
        using (var newBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var locations = newBlockManager.GetBlockLocations();
            reindexStopwatch.Stop();

            // Assert
            Assert.Equal(blockCount, locations.Count);
            _output.WriteLine($"Reindexing {blockCount:N0} blocks took: {reindexStopwatch.ElapsedMilliseconds:N0} ms");
            _output.WriteLine($"Reindexing rate: {blockCount * 1000.0 / reindexStopwatch.ElapsedMilliseconds:F0} blocks/sec");
            
            // Performance should be reasonable (>10k blocks/sec)
            var blocksPerSecond = blockCount * 1000.0 / reindexStopwatch.ElapsedMilliseconds;
            Assert.True(blocksPerSecond > 10000, $"Reindexing too slow: {blocksPerSecond:F0} blocks/sec");
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    [Trait("Size", "Large")]
    public async Task Should_Handle_Large_File_Compaction()
    {
        // Arrange - Create large file with many overwrites
        const int uniqueBlocks = 50_000;
        const int overwritesPerBlock = 5;
        const int blockSize = 128;

        using (var blockManager = new RawBlockManager(_testFile))
        {
            _output.WriteLine($"Creating {uniqueBlocks * overwritesPerBlock:N0} total blocks...");
            
            for (int overwrite = 0; overwrite < overwritesPerBlock; overwrite++)
            {
                for (int blockNum = 0; blockNum < uniqueBlocks; blockNum++)
                {
                    var block = new Block
                    {
                        Version = 1,
                        Type = BlockType.Segment,
                        Flags = 0,
                        Encoding = PayloadEncoding.RawBytes,
                        Timestamp = DateTime.UtcNow.Ticks + (overwrite * 1000000L) + blockNum,
                        BlockId = 400_000 + blockNum,
                        Payload = new byte[blockSize]
                    };

                    // Fill with pattern based on overwrite iteration
                    for (int j = 0; j < blockSize; j++)
                    {
                        block.Payload[j] = (byte)((overwrite * 100 + blockNum + j) % 256);
                    }

                    await blockManager.WriteBlockAsync(block);
                }

                var currentSize = new FileInfo(_testFile).Length;
                _output.WriteLine($"Overwrite {overwrite + 1}/{overwritesPerBlock} complete, File size: {currentSize / 1048576.0:F1} MB");
            }

            var fileSizeBeforeCompact = new FileInfo(_testFile).Length;
            _output.WriteLine($"File size before compaction: {fileSizeBeforeCompact / 1048576.0:F1} MB");

            // Act - Compact the large file
            var compactStopwatch = Stopwatch.StartNew();
            await blockManager.CompactAsync();
            compactStopwatch.Stop();

            var fileSizeAfterCompact = new FileInfo(_testFile).Length;
            var spaceReclaimed = fileSizeBeforeCompact - fileSizeAfterCompact;
            var reclaimPercentage = (double)spaceReclaimed / fileSizeBeforeCompact * 100;

            // Assert
            _output.WriteLine($"Compaction took: {compactStopwatch.Elapsed.TotalSeconds:F1} seconds");
            _output.WriteLine($"File size after compaction: {fileSizeAfterCompact / 1048576.0:F1} MB");
            _output.WriteLine($"Space reclaimed: {spaceReclaimed / 1048576.0:F1} MB ({reclaimPercentage:F1}%)");

            // Should reclaim significant space
            Assert.True(reclaimPercentage > 70, $"Should reclaim >70% space, but only reclaimed {reclaimPercentage:F1}%");

            // Verify all blocks are still accessible
            var locations = blockManager.GetBlockLocations();
            Assert.Equal(uniqueBlocks, locations.Count);

            // Test a few random blocks
            var random = new Random(42);
            for (int i = 0; i < 100; i++)
            {
                var testBlockId = 400_000 + random.Next(uniqueBlocks);
                var readResult = await blockManager.ReadBlockAsync(testBlockId);
                Assert.True(readResult.IsSuccess, $"Block {testBlockId} should be readable after compaction");
            }
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    [Trait("Size", "Memory")]
    public async Task Should_Maintain_Reasonable_Memory_Usage_With_Large_Index()
    {
        // Arrange - Test memory efficiency with large number of blocks
        const int blockCount = 200_000;
        
        var initialMemory = GC.GetTotalMemory(true);
        _output.WriteLine($"Initial memory usage: {initialMemory / 1048576.0:F1} MB");

        // Act
        using (var blockManager = new RawBlockManager(_testFile))
        {
            for (int i = 0; i < blockCount; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + i,
                    BlockId = 500_000 + i,
                    Payload = new byte[32] // Small payload
                };

                await blockManager.WriteBlockAsync(block);

                if (i % 50000 == 0 && i > 0)
                {
                    var currentMemory = GC.GetTotalMemory(true);
                    _output.WriteLine($"Memory at {i:N0} blocks: {currentMemory / 1048576.0:F1} MB");
                }
            }

            var finalMemory = GC.GetTotalMemory(true);
            var memoryIncrease = finalMemory - initialMemory;
            
            _output.WriteLine($"Final memory usage: {finalMemory / 1048576.0:F1} MB");
            _output.WriteLine($"Memory increase: {memoryIncrease / 1048576.0:F1} MB for {blockCount:N0} blocks");
            _output.WriteLine($"Memory per block: {(double)memoryIncrease / blockCount:F1} bytes");

            // Assert - Memory usage should be reasonable (index shouldn't be huge)
            var bytesPerBlock = (double)memoryIncrease / blockCount;
            Assert.True(bytesPerBlock < 1000, $"Memory usage too high: {bytesPerBlock:F1} bytes per block");

            // Verify index is working
            var locations = blockManager.GetBlockLocations();
            Assert.Equal(blockCount, locations.Count);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_testFile))
        {
            try
            {
                File.Delete(_testFile);
                _output.WriteLine($"Deleted test file: {_testFile}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Warning: Could not delete test file {_testFile}: {ex.Message}");
            }
        }
    }
}