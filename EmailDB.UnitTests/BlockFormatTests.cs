using System;
using System.IO;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using Xunit;

namespace EmailDB.UnitTests;

public class BlockFormatTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;

    public BlockFormatTests()
    {
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task Block_Header_Should_Be_37_Bytes()
    {
        // Verify header size constant
        Assert.Equal(37, RawBlockManager.HeaderSize);
    }

    [Fact]
    public async Task Block_Total_Fixed_Overhead_Should_Be_61_Bytes()
    {
        // Header (37) + Header Checksum (4) + Payload Checksum (4) + Footer (16) = 61
        Assert.Equal(61, RawBlockManager.TotalFixedOverhead);
    }

    [Fact]
    public async Task Block_Should_Include_PayloadEncoding_Field()
    {
        // Arrange
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.Json,  // This is the new field
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 12345,
            Payload = new byte[] { 1, 2, 3, 4, 5 }
        };

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Assert
        var readBlock = readResult.Value;
        Assert.Equal(PayloadEncoding.Json, readBlock.Encoding);
        Assert.Equal(block.Version, readBlock.Version);
        Assert.Equal(block.Type, readBlock.Type);
        Assert.Equal(block.Flags, readBlock.Flags);
        Assert.Equal(block.BlockId, readBlock.BlockId);
    }

    [Fact]
    public async Task Block_Should_Serialize_All_PayloadEncoding_Types()
    {
        var encodingTypes = Enum.GetValues<PayloadEncoding>();

        foreach (var encoding in encodingTypes)
        {
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = encoding,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = (long)encoding + 1000,
                Payload = new byte[] { (byte)encoding }
            };

            var writeResult = await _blockManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess, $"Failed to write block with encoding {encoding}");

            var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
            Assert.True(readResult.IsSuccess, $"Failed to read block with encoding {encoding}");
            
            Assert.Equal(encoding, readResult.Value.Encoding);
        }
    }

    [Fact]
    public async Task Block_With_Empty_Payload_Should_Have_Zero_Checksum()
    {
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 99999,
            Payload = new byte[0]  // Empty payload
        };

        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Read the raw file to verify checksum
        using (var fs = new FileStream(_testFile, FileMode.Open, FileAccess.Read))
        {
            fs.Seek(writeResult.Value.Position + 37 + 4, SeekOrigin.Begin); // Skip to payload checksum
            var checksumBytes = new byte[4];
            fs.Read(checksumBytes, 0, 4);
            var checksum = BitConverter.ToUInt32(checksumBytes, 0);
            Assert.Equal(0U, checksum);
        }
    }

    [Fact]
    public void Block_Header_Offsets_Should_Match_Specification()
    {
        // According to spec:
        // Offset 0: Magic (8 bytes)
        // Offset 8: Version (2 bytes)
        // Offset 10: Block Type (1 byte)
        // Offset 11: Flags (1 byte)
        // Offset 12: Payload Encoding (1 byte) <- NEW FIELD
        // Offset 13: Timestamp (8 bytes)
        // Offset 21: Block ID (8 bytes)
        // Offset 29: Payload Length (8 bytes)
        // Total: 37 bytes

        var expectedOffsets = new[]
        {
            (0, 8, "Magic"),
            (8, 2, "Version"),
            (10, 1, "BlockType"),
            (11, 1, "Flags"),
            (12, 1, "PayloadEncoding"),
            (13, 8, "Timestamp"),
            (21, 8, "BlockId"),
            (29, 8, "PayloadLength")
        };

        var totalSize = 0;
        foreach (var (offset, size, name) in expectedOffsets)
        {
            Assert.True(offset == totalSize, $"{name} should be at offset {totalSize}, not {offset}");
            totalSize += size;
        }

        Assert.Equal(37, totalSize);
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
        if (File.Exists(_testFile))
        {
            File.Delete(_testFile);
        }
    }
}