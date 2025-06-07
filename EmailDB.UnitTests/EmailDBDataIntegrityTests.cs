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
    public async Task EmailDB_Should_Write_And_Read_Small_Text_Data()
    {
        // Arrange
        var testData = "Hello EmailDB World!";
        var payload = Encoding.UTF8.GetBytes(testData);
        
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

        _output.WriteLine($"Writing text data: '{testData}' ({payload.Length} bytes)");

        // Act - Write
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

        // Act - Read
        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");

        // Assert
        var readData = Encoding.UTF8.GetString(readResult.Value.Payload);
        Assert.Equal(testData, readData);
        Assert.Equal(block.Encoding, readResult.Value.Encoding);
        
        _output.WriteLine($"Successfully read back: '{readData}'");
    }

    [Fact]
    public async Task EmailDB_Should_Store_Multiple_Email_Blocks()
    {
        var emails = new[]
        {
            new { Subject = "Test Email 1", From = "test1@example.com", Body = "This is test email 1 content." },
            new { Subject = "Test Email 2", From = "test2@example.com", Body = "This is test email 2 content with more text." },
            new { Subject = "Test Email 3", From = "test3@example.com", Body = "This is test email 3 with even more content and details." }
        };

        var blockIds = new long[emails.Length];

        // Write all email blocks
        for (int i = 0; i < emails.Length; i++)
        {
            var emailData = $"Subject: {emails[i].Subject}\nFrom: {emails[i].From}\n\n{emails[i].Body}";
            var payload = Encoding.UTF8.GetBytes(emailData);
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = 2000 + i,
                Payload = payload
            };

            var writeResult = await _blockManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
            blockIds[i] = block.BlockId;
            
            _output.WriteLine($"Wrote email {i + 1}: {emails[i].Subject} ({payload.Length} bytes)");
        }

        // Read back all email blocks and verify
        for (int i = 0; i < emails.Length; i++)
        {
            var readResult = await _blockManager.ReadBlockAsync(blockIds[i]);
            Assert.True(readResult.IsSuccess);
            
            var emailData = Encoding.UTF8.GetString(readResult.Value.Payload);
            Assert.Contains(emails[i].Subject, emailData);
            Assert.Contains(emails[i].From, emailData);
            Assert.Contains(emails[i].Body, emailData);
            
            _output.WriteLine($"Verified email {i + 1}: Data integrity confirmed");
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
    public async Task EmailDB_Should_Store_JSON_Encoded_Email_Metadata()
    {
        // Arrange - Create email metadata as JSON
        var metadata = new
        {
            MessageId = "test@example.com",
            Subject = "Important Meeting",
            From = "boss@company.com",
            To = new[] { "employee1@company.com", "employee2@company.com" },
            Date = DateTime.UtcNow,
            AttachmentCount = 2,
            Size = 15678
        };

        var jsonPayloadEncoding = new JsonPayloadEncoding();
        var serializeResult = jsonPayloadEncoding.Serialize(metadata);
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
        
        var deserializeResult = jsonPayloadEncoding.Deserialize<dynamic>(readResult.Value.Payload);
        Assert.True(deserializeResult.IsSuccess);
        
        _output.WriteLine("JSON metadata successfully round-tripped through EmailDB");
    }

    [Fact]
    public async Task EmailDB_Should_Handle_Large_Email_Content()
    {
        // Arrange - Create large email content (simulating email with large attachment)
        var largeContent = new StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            largeContent.AppendLine($"Line {i}: This is a large email content to test EmailDB's ability to handle substantial amounts of data.");
        }

        var contentBytes = Encoding.UTF8.GetBytes(largeContent.ToString());
        var originalSize = contentBytes.Length;

        var block = new Block
        {
            Version = 1,
            Type = BlockType.Segment,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 5001,
            Payload = contentBytes
        };

        _output.WriteLine($"Writing large content: {originalSize:N0} bytes");

        // Act
        var writeResult = await _blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);

        var readResult = await _blockManager.ReadBlockAsync(block.BlockId);
        Assert.True(readResult.IsSuccess);

        // Assert
        Assert.Equal(originalSize, readResult.Value.Payload.Length);
        var readContent = Encoding.UTF8.GetString(readResult.Value.Payload);
        Assert.Contains("Line 0:", readContent);
        Assert.Contains("Line 999:", readContent);
        
        _output.WriteLine($"Large content verified: {readResult.Value.Payload.Length:N0} bytes read back correctly");
    }

    [Fact]
    public async Task EmailDB_Should_Maintain_Block_Order_And_Locations()
    {
        var blockIds = new[] { 6001L, 6002L, 6003L, 6004L, 6005L };
        var writePositions = new long[blockIds.Length];

        // Write blocks sequentially
        for (int i = 0; i < blockIds.Length; i++)
        {
            var data = $"Block {i} content with some data to make it interesting.";
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Segment,
                Flags = 0,
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTime.UtcNow.Ticks,
                BlockId = blockIds[i],
                Payload = Encoding.UTF8.GetBytes(data)
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
            
            var readData = Encoding.UTF8.GetString(readResult.Value.Payload);
            Assert.Contains($"Block {i} content", readData);
        }
        
        _output.WriteLine("All blocks maintain correct order and can be read back");
    }

    [Fact]
    public async Task EmailDB_Should_Verify_File_Structure_Integrity()
    {
        // Write a known block
        var testData = "File structure integrity test";
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0,
            Encoding = PayloadEncoding.RawBytes,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = 7001,
            Payload = Encoding.UTF8.GetBytes(testData)
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
        Assert.Equal(testData, Encoding.UTF8.GetString(readResult.Value.Payload));
    }

    [Theory]
    [InlineData(PayloadEncoding.RawBytes)]
    [InlineData(PayloadEncoding.Json)]
    public async Task EmailDB_Should_Preserve_Encoding_Type(PayloadEncoding encoding)
    {
        var testData = "Encoding preservation test";
        byte[] payload;

        if (encoding == PayloadEncoding.Json)
        {
            var jsonEncoder = new JsonPayloadEncoding();
            var serializeResult = jsonEncoder.Serialize(new { message = testData });
            Assert.True(serializeResult.IsSuccess);
            payload = serializeResult.Value;
        }
        else
        {
            payload = Encoding.UTF8.GetBytes(testData);
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