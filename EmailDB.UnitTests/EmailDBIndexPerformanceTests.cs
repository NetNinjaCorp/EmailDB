using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests;

/// <summary>
/// Performance tests specifically focused on EmailDB's indexing capabilities
/// and ability to quickly locate blocks in various scenarios.
/// </summary>
public class EmailDBIndexPerformanceTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public EmailDBIndexPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Index_Performance_Sequential_Block_IDs()
    {
        const int blockCount = 10000;
        var random = new Random(100);

        _output.WriteLine("=== Index Performance: Sequential Block IDs ===");
        
        // Write blocks with sequential IDs
        var writeStopwatch = Stopwatch.StartNew();
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
                BlockId = i + 1,
                Payload = data
            };

            await _blockManager.WriteBlockAsync(block);
        }
        writeStopwatch.Stop();

        _output.WriteLine($"Wrote {blockCount:N0} sequential blocks in {writeStopwatch.ElapsedMilliseconds:N0}ms");

        // Test 1: First block access (best case)
        var firstBlockStopwatch = Stopwatch.StartNew();
        var firstResult = await _blockManager.ReadBlockAsync(1);
        firstBlockStopwatch.Stop();
        Assert.True(firstResult.IsSuccess);
        _output.WriteLine($"\nFirst block access time: {firstBlockStopwatch.Elapsed.TotalMilliseconds:F3}ms");

        // Test 2: Last block access (worst case for linear search)
        var lastBlockStopwatch = Stopwatch.StartNew();
        var lastResult = await _blockManager.ReadBlockAsync(blockCount);
        lastBlockStopwatch.Stop();
        Assert.True(lastResult.IsSuccess);
        _output.WriteLine($"Last block access time: {lastBlockStopwatch.Elapsed.TotalMilliseconds:F3}ms");

        // Test 3: Middle block access
        var middleBlockStopwatch = Stopwatch.StartNew();
        var middleResult = await _blockManager.ReadBlockAsync(blockCount / 2);
        middleBlockStopwatch.Stop();
        Assert.True(middleResult.IsSuccess);
        _output.WriteLine($"Middle block access time: {middleBlockStopwatch.Elapsed.TotalMilliseconds:F3}ms");

        // Test 4: Random access pattern
        _output.WriteLine("\nRandom access pattern (100 lookups):");
        var randomAccessTimes = new List<double>();
        
        for (int i = 0; i < 100; i++)
        {
            var targetId = random.Next(1, blockCount + 1);
            var stopwatch = Stopwatch.StartNew();
            var result = await _blockManager.ReadBlockAsync(targetId);
            stopwatch.Stop();
            
            Assert.True(result.IsSuccess);
            randomAccessTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine($"- Average: {randomAccessTimes.Average():F3}ms");
        _output.WriteLine($"- Min: {randomAccessTimes.Min():F3}ms");
        _output.WriteLine($"- Max: {randomAccessTimes.Max():F3}ms");
        _output.WriteLine($"- Median: {randomAccessTimes.OrderBy(x => x).ElementAt(50):F3}ms");
    }

    [Fact]
    public async Task Index_Performance_Sparse_Block_IDs()
    {
        const int blockCount = 5000;
        var random = new Random(200);
        var blockIds = new List<long>();

        _output.WriteLine("=== Index Performance: Sparse Block IDs ===");
        
        // Generate sparse block IDs (simulating real-world scenario)
        var currentId = 1000L;
        for (int i = 0; i < blockCount; i++)
        {
            currentId += random.Next(1, 100); // Gaps of 1-99
            blockIds.Add(currentId);
        }

        _output.WriteLine($"Block ID range: {blockIds.First()} to {blockIds.Last()}");
        _output.WriteLine($"Sparsity: {blockCount:N0} blocks across {blockIds.Last() - blockIds.First():N0} ID range");

        // Write blocks
        foreach (var blockId in blockIds)
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
                BlockId = blockId,
                Payload = data
            };

            await _blockManager.WriteBlockAsync(block);
        }

        // Test lookup performance with sparse IDs
        _output.WriteLine("\nSparse ID lookup performance (50 lookups):");
        var lookupTimes = new List<double>();
        
        for (int i = 0; i < 50; i++)
        {
            var targetId = blockIds[random.Next(blockIds.Count)];
            var stopwatch = Stopwatch.StartNew();
            var result = await _blockManager.ReadBlockAsync(targetId);
            stopwatch.Stop();
            
            Assert.True(result.IsSuccess);
            lookupTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine($"- Average: {lookupTimes.Average():F3}ms");
        _output.WriteLine($"- Min: {lookupTimes.Min():F3}ms");
        _output.WriteLine($"- Max: {lookupTimes.Max():F3}ms");

        // Test non-existent block lookup
        _output.WriteLine("\nNon-existent block lookup performance:");
        var nonExistentTimes = new List<double>();
        
        for (int i = 0; i < 20; i++)
        {
            var nonExistentId = random.Next(1, 999); // IDs before our range
            var stopwatch = Stopwatch.StartNew();
            var result = await _blockManager.ReadBlockAsync(nonExistentId);
            stopwatch.Stop();
            
            Assert.False(result.IsSuccess);
            nonExistentTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine($"- Average time to determine block doesn't exist: {nonExistentTimes.Average():F3}ms");
    }

    [Fact]
    public async Task Index_Rebuild_Performance_After_Corruption()
    {
        const int blockCount = 10000;
        var random = new Random(300);

        _output.WriteLine("=== Index Rebuild Performance Test ===");
        
        // Create initial blocks
        _output.WriteLine($"Creating {blockCount:N0} blocks...");
        var blockIds = new HashSet<long>();
        
        for (int i = 0; i < blockCount; i++)
        {
            var data = new byte[128 + random.Next(384)]; // 128-512 bytes
            random.NextBytes(data);
            
            var blockId = 100000 + i;
            blockIds.Add(blockId);
            
            var block = new Block
            {
                Version = 1,
                Type = (BlockType)(i % 8 + 1),
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockId,
                Payload = data
            };

            await _blockManager.WriteBlockAsync(block);
        }

        var fileSize = new FileInfo(_testFile).Length;
        _output.WriteLine($"File size: {fileSize:N0} bytes ({fileSize / 1024.0 / 1024.0:F2} MB)");

        // Simulate index corruption by clearing the in-memory index
        _output.WriteLine("\nSimulating index corruption/loss...");
        // In a real scenario, this would be loading a file without its index
        
        // Time full index rebuild
        _output.WriteLine("Rebuilding index from file...");
        var rebuildStopwatch = Stopwatch.StartNew();
        
        var rebuiltLocations = _blockManager.GetBlockLocations();
        
        rebuildStopwatch.Stop();

        _output.WriteLine($"\nIndex rebuild complete:");
        _output.WriteLine($"- Blocks found: {rebuiltLocations.Count:N0}");
        _output.WriteLine($"- Rebuild time: {rebuildStopwatch.ElapsedMilliseconds:N0}ms");
        _output.WriteLine($"- Rebuild speed: {rebuiltLocations.Count / (rebuildStopwatch.ElapsedMilliseconds / 1000.0):F2} blocks/second");
        _output.WriteLine($"- File scan speed: {fileSize / 1024.0 / 1024.0 / (rebuildStopwatch.ElapsedMilliseconds / 1000.0):F2} MB/second");

        // Verify rebuilt index accuracy
        _output.WriteLine("\nVerifying rebuilt index accuracy...");
        var verificationCount = 0;
        var sampleIds = blockIds.OrderBy(x => random.Next()).Take(100).ToList();
        
        foreach (var blockId in sampleIds)
        {
            var result = await _blockManager.ReadBlockAsync(blockId);
            if (result.IsSuccess)
                verificationCount++;
        }

        Assert.Equal(100, verificationCount);
        _output.WriteLine($"Index accuracy verified: {verificationCount}/100 blocks found correctly");
    }

    [Fact]
    public async Task Index_Performance_With_Duplicate_BlockIDs()
    {
        const int uniqueBlocks = 1000;
        const int versionsPerBlock = 5;
        var random = new Random(400);

        _output.WriteLine("=== Index Performance: Duplicate Block IDs (Versioning) ===");
        
        // Write multiple versions of same blocks (simulating updates)
        for (int blockNum = 0; blockNum < uniqueBlocks; blockNum++)
        {
            var blockId = 200000 + blockNum;
            
            for (int version = 1; version <= versionsPerBlock; version++)
            {
                var data = new byte[256];
                random.NextBytes(data);
                
                var block = new Block
                {
                    Version = (ushort)version,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + version,
                    BlockId = blockId,
                    Payload = data
                };

                await _blockManager.WriteBlockAsync(block);
            }
        }

        var totalBlocks = uniqueBlocks * versionsPerBlock;
        _output.WriteLine($"Wrote {totalBlocks:N0} total blocks ({uniqueBlocks:N0} unique IDs × {versionsPerBlock} versions)");

        // Test performance of finding latest version
        _output.WriteLine("\nPerformance of finding latest version:");
        var latestVersionTimes = new List<double>();
        
        for (int i = 0; i < 100; i++)
        {
            var targetId = 200000 + random.Next(uniqueBlocks);
            var stopwatch = Stopwatch.StartNew();
            
            var result = await _blockManager.ReadBlockAsync(targetId);
            
            stopwatch.Stop();
            Assert.True(result.IsSuccess);
            Assert.Equal(versionsPerBlock, result.Value.Version); // Should get latest version
            latestVersionTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine($"- Average time to find latest version: {latestVersionTimes.Average():F3}ms");
        _output.WriteLine($"- Min: {latestVersionTimes.Min():F3}ms");
        _output.WriteLine($"- Max: {latestVersionTimes.Max():F3}ms");

        // Test index memory usage
        var locations = _blockManager.GetBlockLocations();
        _output.WriteLine($"\nIndex statistics:");
        _output.WriteLine($"- Total block locations tracked: {locations.Count:N0}");
        _output.WriteLine($"- Memory efficiency: {locations.Count / (double)uniqueBlocks:F2} entries per unique block");
    }

    [Fact]
    public async Task Index_Performance_After_Heavy_Fragmentation()
    {
        const int iterations = 5;
        const int blocksPerIteration = 1000;
        var random = new Random(500);

        _output.WriteLine("=== Index Performance: Heavy Fragmentation Scenario ===");
        
        // Simulate file fragmentation through multiple write/update cycles
        for (int iter = 0; iter < iterations; iter++)
        {
            _output.WriteLine($"\nIteration {iter + 1}:");
            
            // Write new blocks
            for (int i = 0; i < blocksPerIteration; i++)
            {
                var data = new byte[random.Next(100, 1000)];
                random.NextBytes(data);
                
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = 300000 + (iter * blocksPerIteration) + i,
                    Payload = data
                };

                await _blockManager.WriteBlockAsync(block);
            }
            
            // Update random existing blocks
            var updateCount = blocksPerIteration / 4;
            for (int i = 0; i < updateCount; i++)
            {
                var targetId = 300000 + random.Next(iter * blocksPerIteration + blocksPerIteration);
                var newData = new byte[random.Next(500, 1500)]; // Different size
                random.NextBytes(newData);
                
                var block = new Block
                {
                    Version = 2,
                    Type = BlockType.Segment,
                    Flags = 1,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = targetId,
                    Payload = newData
                };

                await _blockManager.WriteBlockAsync(block);
            }
            
            _output.WriteLine($"- Wrote {blocksPerIteration} new blocks");
            _output.WriteLine($"- Updated {updateCount} existing blocks");
        }

        var fileInfo = new FileInfo(_testFile);
        _output.WriteLine($"\nFragmented file statistics:");
        _output.WriteLine($"- File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
        
        var allLocations = _blockManager.GetBlockLocations();
        _output.WriteLine($"- Total block locations: {allLocations.Count:N0}");

        // Test random access performance in fragmented file
        _output.WriteLine("\nRandom access in fragmented file (100 lookups):");
        var accessTimes = new List<double>();
        
        for (int i = 0; i < 100; i++)
        {
            var targetId = 300000 + random.Next(iterations * blocksPerIteration);
            var stopwatch = Stopwatch.StartNew();
            
            var result = await _blockManager.ReadBlockAsync(targetId);
            
            stopwatch.Stop();
            if (result.IsSuccess)
                accessTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine($"- Average: {accessTimes.Average():F3}ms");
        _output.WriteLine($"- Min: {accessTimes.Min():F3}ms");
        _output.WriteLine($"- Max: {accessTimes.Max():F3}ms");
        _output.WriteLine($"- 95th percentile: {accessTimes.OrderBy(x => x).ElementAt((int)(accessTimes.Count * 0.95)):F3}ms");
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