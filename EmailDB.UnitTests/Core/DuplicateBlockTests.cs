using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;
using Xunit.Abstractions;

namespace EmailDB.UnitTests.Core;

/// <summary>
/// Tests for EmailDB duplicate block handling and versioning scenarios.
/// Verifies that block overwrites work correctly and latest versions are always returned.
/// </summary>
public class DuplicateBlockTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public DuplicateBlockTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Should_Return_Latest_Version_When_Block_ID_Overwritten()
    {
        // Arrange
        const long blockId = 1001;
        var firstPayload = new byte[] { 0x01, 0x02, 0x03 };
        var secondPayload = new byte[] { 0x04, 0x05, 0x06, 0x07 };
        var thirdPayload = new byte[] { 0x08, 0x09 };

        // Act - Write multiple versions of the same block ID
        var firstBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = firstPayload
        };
        var firstResult = await _blockManager.WriteBlockAsync(firstBlock);
        Assert.True(firstResult.IsSuccess);

        await Task.Delay(1); // Ensure different timestamps

        var secondBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = secondPayload
        };
        var secondResult = await _blockManager.WriteBlockAsync(secondBlock);
        Assert.True(secondResult.IsSuccess);

        await Task.Delay(1); // Ensure different timestamps

        var thirdBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = thirdPayload
        };
        var thirdResult = await _blockManager.WriteBlockAsync(thirdBlock);
        Assert.True(thirdResult.IsSuccess);

        // Assert - Should return the latest version (third)
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(thirdPayload, readResult.Value.Payload);

        // Index should only contain one entry for this block ID
        var locations = _blockManager.GetBlockLocations();
        Assert.Single(locations);
        Assert.True(locations.ContainsKey(blockId));

        _output.WriteLine($"Latest version has payload length: {readResult.Value.Payload.Length}");
    }

    [Fact]
    public async Task Should_Handle_Same_Timestamp_Duplicates()
    {
        // Arrange - Create blocks with identical timestamps
        const long blockId = 2001;
        var fixedTimestamp = DateTime.UtcNow.Ticks;
        var firstPayload = new byte[] { 0xAA, 0xBB };
        var secondPayload = new byte[] { 0xCC, 0xDD, 0xEE };

        // Act - Write two blocks with same ID and timestamp
        var firstBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = fixedTimestamp,
            BlockId = blockId,
            Payload = firstPayload
        };
        await _blockManager.WriteBlockAsync(firstBlock);

        var secondBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = fixedTimestamp, // Same timestamp
            BlockId = blockId,
            Payload = secondPayload
        };
        await _blockManager.WriteBlockAsync(secondBlock);

        // Assert - Should return one of the versions (likely the last written)
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        
        // In case of identical timestamps, behavior may be implementation-defined
        // But should return a valid payload that matches one of the written blocks
        Assert.True(
            readResult.Value.Payload.SequenceEqual(firstPayload) || 
            readResult.Value.Payload.SequenceEqual(secondPayload),
            "Should return one of the written payloads");

        _output.WriteLine($"With same timestamp, returned payload: [{string.Join(", ", readResult.Value.Payload.Select(b => $"0x{b:X2}"))}]");
    }

    [Fact]
    public async Task Should_Use_Write_Order_Not_Timestamp_For_Version_Precedence()
    {
        // Arrange
        const long blockId = 3001;
        var earlyTimestamp = DateTime.UtcNow.Ticks - 10000;
        var lateTimestamp = DateTime.UtcNow.Ticks;
        
        var earlyPayload = new byte[] { 0x01, 0x02 };
        var latePayload = new byte[] { 0x03, 0x04, 0x05 };

        // Act - Write in non-chronological order (late timestamp first, then early timestamp)
        var lateTimestampBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = lateTimestamp,
            BlockId = blockId,
            Payload = latePayload
        };
        await _blockManager.WriteBlockAsync(lateTimestampBlock);

        var earlyTimestampBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = earlyTimestamp,
            BlockId = blockId,
            Payload = earlyPayload
        };
        await _blockManager.WriteBlockAsync(earlyTimestampBlock);

        // Assert - Should return the last written version (write order), not latest timestamp
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(earlyPayload, readResult.Value.Payload);
        Assert.Equal(earlyTimestamp, readResult.Value.Timestamp);

        _output.WriteLine($"EmailDB uses write order precedence - returned last written block with timestamp: {earlyTimestamp}");
    }

    [Fact]
    public async Task Should_Handle_Multiple_Block_IDs_With_Overwrites()
    {
        // Arrange - Multiple block IDs, each with multiple versions
        var blockUpdates = new[]
        {
            (Id: 4001L, Payloads: new[] { new byte[] { 0x01 }, new byte[] { 0x02 }, new byte[] { 0x03 } }),
            (Id: 4002L, Payloads: new[] { new byte[] { 0x04, 0x05 }, new byte[] { 0x06, 0x07, 0x08 } }),
            (Id: 4003L, Payloads: new[] { new byte[] { 0x09 } }) // Only one version
        };

        // Act - Write all versions
        foreach (var (id, payloads) in blockUpdates)
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
                await Task.Delay(1); // Ensure timestamp differences
            }
        }

        // Assert - Each block ID should return its latest version
        var locations = _blockManager.GetBlockLocations();
        Assert.Equal(blockUpdates.Length, locations.Count);

        foreach (var (id, payloads) in blockUpdates)
        {
            Assert.True(locations.ContainsKey(id));
            
            var readResult = await _blockManager.ReadBlockAsync(id);
            Assert.True(readResult.IsSuccess);
            
            var expectedLatestPayload = payloads[payloads.Length - 1];
            Assert.Equal(expectedLatestPayload, readResult.Value.Payload);
            
            _output.WriteLine($"Block {id} correctly returned latest version with {expectedLatestPayload.Length} bytes");
        }
    }

    [Fact]
    public async Task Should_Handle_Extreme_Duplicate_Scenario()
    {
        // Arrange - Many overwrites of the same block
        const long blockId = 5001;
        const int overwriteCount = 100;
        
        var finalPayload = new byte[] { 0xFF, 0xFE, 0xFD };

        // Act - Write many versions
        for (int i = 0; i < overwriteCount - 1; i++)
        {
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks + i,
                BlockId = blockId,
                Payload = new byte[] { (byte)i }
            };
            await _blockManager.WriteBlockAsync(block);
        }

        // Write final version with distinctive payload
        var finalBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks + overwriteCount,
            BlockId = blockId,
            Payload = finalPayload
        };
        await _blockManager.WriteBlockAsync(finalBlock);

        // Assert - Should return the final version
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(finalPayload, readResult.Value.Payload);

        // Should only have one index entry
        var locations = _blockManager.GetBlockLocations();
        Assert.Single(locations);
        Assert.True(locations.ContainsKey(blockId));

        _output.WriteLine($"After {overwriteCount} overwrites, correctly returned final version");
    }

    [Fact]
    public async Task Should_Handle_Different_Block_Types_With_Same_ID()
    {
        // Arrange - Same block ID but different types (edge case)
        const long blockId = 6001;
        
        var metadataPayload = new byte[] { 0x01, 0x02 };
        var segmentPayload = new byte[] { 0x03, 0x04, 0x05 };

        // Act - Write same ID with different types
        var metadataBlock = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = metadataPayload
        };
        await _blockManager.WriteBlockAsync(metadataBlock);

        await Task.Delay(1);

        var segmentBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = segmentPayload
        };
        await _blockManager.WriteBlockAsync(segmentBlock);

        // Assert - Should return the latest version (by timestamp)
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(segmentPayload, readResult.Value.Payload);
        Assert.Equal(BlockType.Segment, readResult.Value.Type);

        _output.WriteLine($"Correctly handled different block types with same ID");
    }

    [Fact]
    public async Task Should_Handle_Zero_Length_Payload_Overwrites()
    {
        // Arrange
        const long blockId = 7001;
        var originalPayload = new byte[] { 0x01, 0x02, 0x03 };
        var emptyPayload = Array.Empty<byte>();

        // Act - Write original block, then overwrite with empty payload
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

        await Task.Delay(1);

        var emptyBlock = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = blockId,
            Payload = emptyPayload
        };
        await _blockManager.WriteBlockAsync(emptyBlock);

        // Assert - Should return the empty payload version
        var readResult = await _blockManager.ReadBlockAsync(blockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(emptyPayload, readResult.Value.Payload);
        Assert.Empty(readResult.Value.Payload);

        _output.WriteLine("Correctly handled overwrite with zero-length payload");
    }

    [Fact]
    public async Task Should_Maintain_Index_Efficiency_With_Many_Duplicates()
    {
        // Arrange - Test that index doesn't grow excessively with many overwrites
        const int uniqueBlockIds = 10;
        const int overwritesPerBlock = 50;

        // Act - Create many overwrites
        for (int blockNum = 0; blockNum < uniqueBlockIds; blockNum++)
        {
            for (int overwrite = 0; overwrite < overwritesPerBlock; overwrite++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Segment,
                    Flags = 0,
                    Encoding = PayloadEncoding.RawBytes,
                    Timestamp = DateTime.UtcNow.Ticks + (overwrite * 100),
                    BlockId = 8000 + blockNum,
                    Payload = new byte[] { (byte)blockNum, (byte)overwrite }
                };
                await _blockManager.WriteBlockAsync(block);
            }
        }

        // Assert - Index should only contain one entry per unique block ID
        var locations = _blockManager.GetBlockLocations();
        Assert.Equal(uniqueBlockIds, locations.Count);

        // Verify each block returns the latest version
        for (int blockNum = 0; blockNum < uniqueBlockIds; blockNum++)
        {
            long blockId = 8000 + blockNum;
            Assert.True(locations.ContainsKey(blockId));
            
            var readResult = await _blockManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess);
            
            // Should have the last overwrite's data
            var expectedPayload = new byte[] { (byte)blockNum, (byte)(overwritesPerBlock - 1) };
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        _output.WriteLine($"Index efficiently maintained {locations.Count} entries for {uniqueBlockIds * overwritesPerBlock} total writes");
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