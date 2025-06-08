using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;
using Xunit;
using Xunit.Abstractions;
using Force.Crc32;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests to prove that data is being written correctly into EmailDB format
/// and can be reliably read back with full integrity verification.
/// </summary>
public class EmailDBDataIntegrityTests : IDisposable
{
    private readonly string _testFile;
    private readonly RawBlockManager _blockManager;
    private readonly ITestOutputHelper _output;

    public EmailDBDataIntegrityTests(ITestOutputHelper output)
    {
        _output = output;
        _testFile = Path.GetTempFileName();
        _blockManager = new RawBlockManager(_testFile);
    }

    [Fact]
    public async Task EmailDB_Should_Write_And_Read_Small_Binary_Data()
    {
        // Arrange
        var payload = new byte[20];
        new Random(42).NextBytes(payload);
        
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 1001,
            Payload = payload
        };

        _output.WriteLine($"Writing binary data: {payload.Length} bytes");

        // Act - Write
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

        // Act - Read
        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");

        // Assert
        Assert.Equal(payload, readResult.Value.Payload);
        Assert.Equal(block.Encoding, readResult.Value.Encoding);
        
        _output.WriteLine($"Successfully read back {payload.Length} bytes");
    }

    [Fact]
    public async Task EmailDB_Should_Store_Multiple_Data_Blocks()
    {
        const int blockCount = 3;
        var blockIds = new long[blockCount];
        var payloads = new byte[blockCount][];
        var random = new Random(123);

        // Write multiple data blocks with different sizes
        for (int i = 0; i < blockCount; i++)
        {
            // Create binary data with varying sizes
            var size = 100 + (i * 50); // 100, 150, 200 bytes
            payloads[i] = new byte[size];
            random.NextBytes(payloads[i]);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 2000 + i,
                Payload = payloads[i]
            };

            var writeResult = await _blockManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
            blockIds[i] = block.BlockId;
            
            _output.WriteLine($"Wrote block {i + 1}: {size} bytes");
        }

        // Read back all blocks and verify
        for (int i = 0; i < blockCount; i++)
        {
            var readResult = await _blockManager.ReadBlockAsync(blockIds[i]);
            Assert.True(readResult.IsSuccess);
            
            Assert.Equal(payloads[i], readResult.Value.Payload);
            
            _output.WriteLine($"Verified block {i + 1}: Data integrity confirmed");
        }
    }

    [Fact]
    public async Task EmailDB_Should_Store_Binary_Data_With_Integrity()
    {
        // Arrange - Create binary data (simulating email attachment)
        var binaryData = new byte[1024];
        var random = new Random(42); // Fixed seed for reproducible tests
        random.NextBytes(binaryData);
        
        var originalChecksum = Crc32Algorithm.Compute(binaryData);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 3001,
            Payload = binaryData
        };

        _output.WriteLine($"Writing binary data: {binaryData.Length} bytes, CRC32: 0x{originalChecksum:X8}");

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Assert
        var readChecksum = Crc32Algorithm.Compute(readResult.Value.Payload);
        Assert.Equal(originalChecksum, readChecksum);
        Assert.Equal(binaryData.Length, readResult.Value.Payload.Length);
        
        _output.WriteLine($"Binary data verified: CRC32 match, length match");
    }

    [Fact]
    public async Task EmailDB_Should_Store_JSON_Encoded_Metadata()
    {
        // Arrange - Create generic metadata as JSON
        var metadata = new
        {
            Id = Guid.NewGuid().ToString(),
            Type = "DataSegment",
            Version = 1,
            Attributes = new[] { "compressed", "encrypted" },
            Timestamp = DateTime.UtcNow,
            SegmentCount = 5,
            TotalSize = 15678
        };

        var jsonEncoding = new JsonPayloadEncoding();
        var serializeResult = jsonEncoding.Serialize(metadata);
        Assert.True(serializeResult.IsSuccess);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 4001,
            Payload = serializeResult.Value
        };

        _output.WriteLine($"Writing JSON metadata: {Encoding.UTF8.GetString(serializeResult.Value)}");

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Assert
        Assert.Equal(PayloadEncoding.Json, readResult.Value.Encoding);
        
        var deserializeResult = jsonEncoding.Deserialize<dynamic>(readResult.Value.Payload);
        Assert.True(deserializeResult.IsSuccess);
        
        _output.WriteLine("JSON metadata successfully round-tripped through EmailDB");
    }

    [Fact]
    public async Task EmailDB_Should_Handle_Large_Binary_Content()
    {
        // Arrange - Create large binary content
        var largeData = new byte[100 * 1024]; // 100KB
        var random = new Random(456);
        random.NextBytes(largeData);
        
        var originalSize = largeData.Length;
        var originalChecksum = Crc32Algorithm.Compute(largeData);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 5001,
            Payload = largeData
        };

        _output.WriteLine($"Writing large binary data: {originalSize:N0} bytes");

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Assert
        Assert.Equal(originalSize, readResult.Value.Payload.Length);
        var readChecksum = Crc32Algorithm.Compute(readResult.Value.Payload);
        Assert.Equal(originalChecksum, readChecksum);
        
        _output.WriteLine($"Large binary data verified: {readResult.Value.Payload.Length:N0} bytes with matching checksum");
    }

    [Fact]
    public async Task EmailDB_Should_Maintain_Block_Order_And_Locations()
    {
        var blockIds = new[] { 6001L, 6002L, 6003L, 6004L, 6005L };
        var writePositions = new long[blockIds.Length];
        var payloads = new byte[blockIds.Length][];
        var random = new Random(789);

        // Write blocks sequentially with binary data
        for (int i = 0; i < blockIds.Length; i++)
        {
            payloads[i] = new byte[64 + i * 16]; // Varying sizes
            random.NextBytes(payloads[i]);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockIds[i],
                Payload = payloads[i]
            };

            var writeResult = await _blockManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
            writePositions[i] = writeResult.Value.Position;
            
            _output.WriteLine($"Block {blockIds[i]} written at position {writeResult.Value.Position}");
        }

        // Verify blocks are written sequentially
        for (int i = 1; i < writePositions.Length; i++)
        {
            Assert.True(writePositions[i] > writePositions[i-1], 
                $"Block {i} position {writePositions[i]} should be greater than previous block position {writePositions[i-1]}");
        }

        // Verify all blocks can be read back
        for (int i = 0; i < blockIds.Length; i++)
        {
            var readResult = await _blockManager.ReadBlockAsync(blockIds[i]);
            Assert.True(readResult.IsSuccess);
            
            Assert.Equal(payloads[i], readResult.Value.Payload);
        }
        
        _output.WriteLine("All blocks maintain correct order and can be read back");
    }

    [Fact]
    public async Task EmailDB_Should_Verify_File_Structure_Integrity()
    {
        // Write a known block with binary data
        var binaryData = new byte[32];
        new Random(111).NextBytes(binaryData);
        
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 7001,
            Payload = binaryData
        };

        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        // Read raw file and verify structure
        var fileInfo = new FileInfo(_testFile);
        _output.WriteLine($"EmailDB file size: {fileInfo.Length} bytes");

        using (var fs = new FileStream(_testFile, FileMode.Open, FileAccess.Read))
        {
            // Read header magic
            var headerMagic = new byte[8];
            fs.Read(headerMagic, 0, 8);
            var magic = BitConverter.ToUInt64(headerMagic, 0);
            Assert.Equal(RawBlockManager.HEADER_MAGIC, magic);
            
            // Skip to end and read footer magic
            fs.Seek(-16, SeekOrigin.End);
            var footerMagic = new byte[8];
            fs.Read(footerMagic, 0, 8);
            var footerMagicValue = BitConverter.ToUInt64(footerMagic, 0);
            Assert.Equal(RawBlockManager.FOOTER_MAGIC, footerMagicValue);
            
            _output.WriteLine("File structure verified: Valid header and footer magic numbers");
        }

        // Verify block can be read back correctly
        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(binaryData, readResult.Value.Payload);
    }

    [Theory]
    [InlineData(PayloadEncoding.RawBytes)]
    [InlineData(PayloadEncoding.Json)]
    public async Task EmailDB_Should_Preserve_Encoding_Type(PayloadEncoding encoding)
    {
        byte[] payload;

        if (encoding == PayloadEncoding.Json)
        {
            var jsonEncoder = new JsonPayloadEncoding();
            var serializeResult = jsonEncoder.Serialize(new { id = 12345, data = "test", size = 1024 });
            Assert.True(serializeResult.IsSuccess);
            payload = serializeResult.Value;
        }
        else
        {
            payload = new byte[64];
            new Random(222).NextBytes(payload);
        }

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = encoding,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 8000 + (int)encoding,
            Payload = payload
        };

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Assert
        Assert.Equal(encoding, readResult.Value.Encoding);
        _output.WriteLine($"Encoding {encoding} preserved correctly");
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