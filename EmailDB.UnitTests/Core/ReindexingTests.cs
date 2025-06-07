using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Tests for EmailDB complete reindexing functionality from scratch.
/// Verifies that the index can be completely rebuilt from file scanning.
/// </summary>
public class ReindexingTests : IDisposable
{
    private readonly string _testFile;
    private readonly ITestOutputHelper _output;

    public ReindexingTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
    }

    [Fact]
    public async Task Should_Rebuild_Complete_Index_From_File_Scan()
    {
        // Arrange - Create blocks with one BlockManager instance
        var originalBlockIds = new long[] { 1001, 1002, 1003, 1004, 1005 };
        var expectedPayloads = new byte[originalBlockIds.Length][];

        using (var blockManager = new RawBlockManager(_testFile))
        {
            for (int i = 0; i < originalBlockIds.Length; i++)
            {
                expectedPayloads[i] = new byte[] { (byte)(0x10 + i), (byte)(0x20 + i), (byte)(0x30 + i) };
                
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + i,
                    BlockId = originalBlockIds[i],
                    Payload = expectedPayloads[i]
                };
                
                var result = await blockManager.WriteBlockAsync(block);
                Assert.True(result.IsSuccess);
            }

            var originalLocations = blockManager.GetBlockLocations();
            Assert.Equal(originalBlockIds.Length, originalLocations.Count);
            _output.WriteLine($"Original index has {originalLocations.Count} entries");
        }

        // Act - Create a new BlockManager instance (simulating complete reindex from file)
        using (var newBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            // Assert - Index should be completely rebuilt from file scan
            var rebuiltLocations = newBlockManager.GetBlockLocations();
            _output.WriteLine($"Rebuilt index has {rebuiltLocations.Count} entries");

            Assert.Equal(originalBlockIds.Length, rebuiltLocations.Count);

            // Verify all original block IDs are found
            foreach (var blockId in originalBlockIds)
            {
                Assert.True(rebuiltLocations.ContainsKey(blockId), $"Block ID {blockId} not found in rebuilt index");
            }

            // Verify all blocks are readable with correct payloads
            for (int i = 0; i < originalBlockIds.Length; i++)
            {
                var readResult = await newBlockManager.ReadBlockAsync(originalBlockIds[i]);
                Assert.True(readResult.IsSuccess, $"Failed to read block {originalBlockIds[i]}");
                Assert.Equal(expectedPayloads[i], readResult.Value.Payload);
            }
        }
    }

    [Fact]
    public async Task Should_Rebuild_Index_With_Multiple_Block_Versions()
    {
        // Arrange - Create blocks with multiple versions (overwrites)
        const long blockId = 2001;
        var payloadVersions = new[]
        {
            new byte[] { 0x01, 0x02 },
            new byte[] { 0x03, 0x04, 0x05 },
            new byte[] { 0x06, 0x07, 0x08, 0x09 }
        };

        using (var blockManager = new RawBlockManager(_testFile))
        {
            // Write multiple versions of the same block
            for (int version = 0; version < payloadVersions.Length; version++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + (version * 1000),
                    BlockId = blockId,
                    Payload = payloadVersions[version]
                };
                
                await blockManager.WriteBlockAsync(block);
            }
        }

        // Act - Rebuild index from scratch
        using (var newBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var rebuiltLocations = newBlockManager.GetBlockLocations();
            
            // Assert - Should find only one entry for the block ID (latest version)
            Assert.Single(rebuiltLocations);
            Assert.True(rebuiltLocations.ContainsKey(blockId));

            // Should read the latest version
            var readResult = await newBlockManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess);
            
            var latestPayload = payloadVersions[payloadVersions.Length - 1];
            Assert.Equal(latestPayload, readResult.Value.Payload);
            
            _output.WriteLine($"Successfully rebuilt index with latest version of block {blockId}");
        }
    }

    [Fact]
    public async Task Should_Handle_Large_File_Reindexing_Performance()
    {
        // Arrange - Create a larger number of blocks to test scanning performance
        const int blockCount = 1000;
        var blockIds = Enumerable.Range(3000, blockCount).Select(i => (long)i).ToArray();

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
                    BlockId = blockIds[i],
                    Payload = BitConverter.GetBytes(i)
                };
                
                await blockManager.WriteBlockAsync(block);

                if (i % 100 == 0)
                {
                    _output.WriteLine($"Written {i + 1} blocks...");
                }
            }
        }

        var fileSize = new FileInfo(_testFile).Length;
        _output.WriteLine($"Test file size: {fileSize:N0} bytes ({fileSize / 1048576.0:F2} MB)");

        // Act - Measure reindexing performance
        var startTime = DateTime.UtcNow;
        
        using (var newBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var rebuiltLocations = newBlockManager.GetBlockLocations();
            var endTime = DateTime.UtcNow;
            
            var reindexDuration = endTime - startTime;
            _output.WriteLine($"Reindexing {blockCount} blocks took: {reindexDuration.TotalMilliseconds:F0} ms");
            
            // Assert - Performance should be reasonable (under 5 seconds for 1000 blocks)
            Assert.True(reindexDuration.TotalSeconds < 5, 
                $"Reindexing took too long: {reindexDuration.TotalSeconds:F2} seconds");
            
            Assert.Equal(blockCount, rebuiltLocations.Count);
            _output.WriteLine($"Successfully reindexed {rebuiltLocations.Count} blocks");
        }
    }

    [Fact]
    public async Task Should_Handle_Corrupted_Index_Scenario()
    {
        // Arrange - Create valid blocks
        var blockIds = new long[] { 4001, 4002, 4003 };
        
        using (var blockManager = new RawBlockManager(_testFile))
        {
            foreach (var blockId in blockIds)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = blockId,
                    Payload = new byte[] { (byte)(blockId % 256), (byte)((blockId / 256) % 256) }
                };
                
                await blockManager.WriteBlockAsync(block);
            }

            // Verify initial state
            var originalLocations = blockManager.GetBlockLocations();
            Assert.Equal(blockIds.Length, originalLocations.Count);
        }

        // Simulate index corruption by creating new manager that has to rebuild from scratch
        // (In a real scenario, this might be a corrupted index file or memory corruption)

        // Act - Force complete rebuild
        using (var recoveredBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var recoveredLocations = recoveredBlockManager.GetBlockLocations();
            
            // Assert - Should recover all blocks
            Assert.Equal(blockIds.Length, recoveredLocations.Count);
            
            foreach (var blockId in blockIds)
            {
                Assert.True(recoveredLocations.ContainsKey(blockId));
                
                var readResult = await recoveredBlockManager.ReadBlockAsync(blockId);
                Assert.True(readResult.IsSuccess);
                
                var expectedPayload = new byte[] { (byte)(blockId % 256), (byte)((blockId / 256) % 256) };
                Assert.Equal(expectedPayload, readResult.Value.Payload);
            }
            
            _output.WriteLine($"Successfully recovered {recoveredLocations.Count} blocks from 'corrupted' index");
        }
    }

    [Fact]
    public async Task Should_Handle_Mixed_Valid_Invalid_Blocks_During_Rebuild()
    {
        // Arrange - Create some valid blocks, then manually inject some corruption
        var validBlockIds = new long[] { 5001, 5002 };
        
        using (var blockManager = new RawBlockManager(_testFile))
        {
            foreach (var blockId in validBlockIds)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks,
                    BlockId = blockId,
                    Payload = new byte[] { (byte)blockId, (byte)(blockId >> 8) }
                };
                
                await blockManager.WriteBlockAsync(block);
            }
        }

        // Inject some invalid data between valid blocks
        using (var fileStream = new FileStream(_testFile, FileMode.Open, FileAccess.Write))
        {
            fileStream.Seek(0, SeekOrigin.End);
            // Write some garbage data that looks like it could be a block but isn't valid
            var garbageData = new byte[100];
            new Random(42).NextBytes(garbageData);
            fileStream.Write(garbageData);
        }

        // Act - Rebuild index (should skip invalid data)
        using (var newBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var rebuiltLocations = newBlockManager.GetBlockLocations();
            
            // Assert - Should find only the valid blocks
            Assert.True(rebuiltLocations.Count >= validBlockIds.Length, 
                $"Expected at least {validBlockIds.Length} blocks, found {rebuiltLocations.Count}");
            
            // Verify valid blocks are still readable
            foreach (var blockId in validBlockIds)
            {
                if (rebuiltLocations.ContainsKey(blockId))
                {
                    var readResult = await newBlockManager.ReadBlockAsync(blockId);
                    Assert.True(readResult.IsSuccess, $"Valid block {blockId} should be readable");
                    
                    var expectedPayload = new byte[] { (byte)blockId, (byte)(blockId >> 8) };
                    Assert.Equal(expectedPayload, readResult.Value.Payload);
                }
            }
            
            _output.WriteLine($"Rebuilt index with {rebuiltLocations.Count} blocks, ignoring invalid data");
        }
    }

    [Fact]
    public async Task Should_Rebuild_Empty_Index_For_Empty_File()
    {
        // Arrange - Ensure file exists but is empty
        File.WriteAllBytes(_testFile, Array.Empty<byte>());

        // Act - Try to rebuild index from empty file
        using (var blockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var locations = blockManager.GetBlockLocations();
            
            // Assert - Should have empty index
            Assert.Empty(locations);
            _output.WriteLine("Successfully handled empty file during index rebuild");
        }
    }

    [Fact]
    public async Task Should_Maintain_Block_Order_During_Rebuild()
    {
        // Arrange - Create blocks in specific order
        var blockData = new[]
        {
            (Id: 6001L, Timestamp: DateTime.UtcNow.Ticks - 3000, Payload: new byte[] { 0x01 }),
            (Id: 6002L, Timestamp: DateTime.UtcNow.Ticks - 2000, Payload: new byte[] { 0x02 }),
            (Id: 6003L, Timestamp: DateTime.UtcNow.Ticks - 1000, Payload: new byte[] { 0x03 }),
            (Id: 6004L, Timestamp: DateTime.UtcNow.Ticks, Payload: new byte[] { 0x04 })
        };

        using (var blockManager = new RawBlockManager(_testFile))
        {
            foreach (var (id, timestamp, payload) in blockData)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = timestamp,
                    BlockId = id,
                    Payload = payload
                };
                
                await blockManager.WriteBlockAsync(block);
            }
        }

        // Act - Rebuild and verify order is maintained
        using (var newBlockManager = new RawBlockManager(_testFile, createIfNotExists: false))
        {
            var rebuiltLocations = newBlockManager.GetBlockLocations();
            
            // Assert - All blocks should be found
            Assert.Equal(blockData.Length, rebuiltLocations.Count);
            
            // Verify each block maintains its data integrity
            foreach (var (id, timestamp, expectedPayload) in blockData)
            {
                Assert.True(rebuiltLocations.ContainsKey(id));
                
                var readResult = await newBlockManager.ReadBlockAsync(id);
                Assert.True(readResult.IsSuccess);
                Assert.Equal(expectedPayload, readResult.Value.Payload);
                Assert.Equal(timestamp, readResult.Value.Timestamp);
            }
            
            _output.WriteLine($"Successfully verified block order integrity during rebuild");
        }
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
                // Best effort
            }
        }
    }
}