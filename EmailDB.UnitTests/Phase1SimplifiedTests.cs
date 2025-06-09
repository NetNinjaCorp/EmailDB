using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using MimeKit;

namespace EmailDB.UnitTests;

/// <summary>
/// Simplified Phase 1 tests that work with actual APIs
/// </summary>
public class Phase1SimplifiedTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;

    public Phase1SimplifiedTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"Phase1Simple_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _testDbPath = Path.Combine(_testDirectory, "test.emdb");
    }

    [Fact]
    public async Task RawBlockManager_BasicWriteAndRead()
    {
        using var blockManager = new RawBlockManager(_testDbPath, createIfNotExists: true);
        
        // Create a simple block
        var block = new Block
        {
            BlockId = 100,
            Type = BlockType.Folder,
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = Encoding.UTF8.GetBytes("{\"test\":\"data\"}"),
            Version = 1,
            Flags = 0
        };
        
        // Write block
        var writeResult = await blockManager.WriteBlockAsync(block);
        Assert.True(writeResult.IsSuccess);
        
        // Read block back
        var readResult = await blockManager.ReadBlockAsync(100);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(100, readResult.Value.BlockId);
        Assert.Equal(BlockType.Folder, readResult.Value.Type);
    }

    [Fact]
    public void PayloadSerializers_WorkCorrectly()
    {
        // Test JSON serializer
        var jsonSerializer = new JsonPayloadEncoding();
        var testData = new FolderContent { FolderId = 1, Name = "Test", Version = 1 };
        
        var jsonResult = jsonSerializer.Serialize(testData);
        Assert.True(jsonResult.IsSuccess);
        
        var jsonDeserialized = jsonSerializer.Deserialize<FolderContent>(jsonResult.Value);
        Assert.True(jsonDeserialized.IsSuccess);
        Assert.Equal("Test", jsonDeserialized.Value.Name);
        
        // Test Protobuf serializer
        var protobufSerializer = new ProtobufPayloadEncoding();
        var protobufResult = protobufSerializer.Serialize(testData);
        Assert.True(protobufResult.IsSuccess);
        
        var protobufDeserialized = protobufSerializer.Deserialize<FolderContent>(protobufResult.Value);
        Assert.True(protobufDeserialized.IsSuccess);
        Assert.Equal("Test", protobufDeserialized.Value.Name);
    }

    [Fact]
    public void EmailBlockBuilder_AddsEmailsCorrectly()
    {
        var builder = new EmailBlockBuilder(50);
        
        // Create test email
        var message = new MimeMessage();
        message.MessageId = "test@example.com";
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Test content" };
        
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var emailData = stream.ToArray();
        
        // Add email
        var entry = builder.AddEmail(message, emailData);
        Assert.Equal(0, entry.LocalId);
        Assert.NotNull(entry.EnvelopeHash);
        Assert.NotNull(entry.ContentHash);
        Assert.Equal(1, builder.EmailCount);
        
        // Test serialization
        var serialized = builder.SerializeBlock();
        Assert.NotNull(serialized);
        Assert.True(serialized.Length > 0);
    }

    [Fact]
    public void BlockFlags_ExtensionMethods_Work()
    {
        var flags = BlockFlags.None;
        
        // Test compression
        flags = flags.SetCompressionAlgorithm(CompressionAlgorithm.LZ4);
        Assert.Equal(CompressionAlgorithm.LZ4, flags.GetCompressionAlgorithm());
        
        // Test encryption
        flags = flags.SetEncryptionAlgorithm(EncryptionAlgorithm.AES256_GCM);
        Assert.Equal(EncryptionAlgorithm.AES256_GCM, flags.GetEncryptionAlgorithm());
    }

    [Fact]
    public void AdaptiveBlockSizer_CalculatesSizesCorrectly()
    {
        var sizer = new AdaptiveBlockSizer();
        
        // Test different database sizes
        Assert.Equal(50, sizer.GetTargetBlockSizeMB(1L * 1024 * 1024 * 1024)); // 1GB
        Assert.Equal(100, sizer.GetTargetBlockSizeMB(10L * 1024 * 1024 * 1024)); // 10GB
        Assert.Equal(250, sizer.GetTargetBlockSizeMB(50L * 1024 * 1024 * 1024)); // 50GB
        Assert.Equal(500, sizer.GetTargetBlockSizeMB(200L * 1024 * 1024 * 1024)); // 200GB
        Assert.Equal(1024, sizer.GetTargetBlockSizeMB(600L * 1024 * 1024 * 1024)); // 600GB
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}