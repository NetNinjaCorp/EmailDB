using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using MimeKit;

namespace EmailDB.UnitTests;

public class Phase1ComponentTests
{
    [Fact]
    public void BlockType_NewEnums_AreCorrectlyDefined()
    {
        // Verify new block types
        Assert.Equal(9, (byte)BlockType.FolderEnvelope);
        Assert.Equal(10, (byte)BlockType.EmailBatch);
        Assert.Equal(11, (byte)BlockType.KeyManager);
        Assert.Equal(12, (byte)BlockType.KeyExchange);
    }

    [Fact]
    public void EmailEnvelope_CreatesCorrectly()
    {
        var envelope = new EmailEnvelope
        {
            CompoundId = "123:0",
            MessageId = "test@example.com",
            Subject = "Test Subject",
            From = "sender@example.com",
            To = "recipient@example.com",
            Date = DateTime.UtcNow,
            Size = 1024,
            HasAttachments = false,
            Flags = 0,
            EnvelopeHash = new byte[] { 1, 2, 3 }
        };

        Assert.Equal("123:0", envelope.CompoundId);
        Assert.Equal("Test Subject", envelope.Subject);
        Assert.Equal(1024, envelope.Size);
        Assert.False(envelope.HasAttachments);
    }

    [Fact]
    public void FolderEnvelopeBlock_ImplementsBlockContent()
    {
        var block = new FolderEnvelopeBlock
        {
            FolderPath = "/Inbox",
            Version = 1,
            LastModified = DateTime.UtcNow,
            PreviousBlockId = null
        };

        Assert.IsAssignableFrom<BlockContent>(block);
        Assert.Equal(BlockType.FolderEnvelope, block.GetBlockType());
        Assert.Equal("/Inbox", block.FolderPath);
        Assert.NotNull(block.Envelopes);
    }

    [Fact]
    public void AdaptiveBlockSizer_ReturnsCorrectSizes()
    {
        var sizer = new AdaptiveBlockSizer();

        // Test size progression
        Assert.Equal(50, sizer.GetTargetBlockSizeMB(1L * 1024 * 1024 * 1024)); // 1GB
        Assert.Equal(100, sizer.GetTargetBlockSizeMB(10L * 1024 * 1024 * 1024)); // 10GB
        Assert.Equal(250, sizer.GetTargetBlockSizeMB(50L * 1024 * 1024 * 1024)); // 50GB
        Assert.Equal(500, sizer.GetTargetBlockSizeMB(200L * 1024 * 1024 * 1024)); // 200GB
        Assert.Equal(1024, sizer.GetTargetBlockSizeMB(600L * 1024 * 1024 * 1024)); // 600GB
    }

    [Fact]
    public void EmailBlockBuilder_TracksSize()
    {
        var builder = new EmailBlockBuilder(50); // 50MB target
        Assert.Equal(50 * 1024 * 1024, builder.TargetSize);
        Assert.Equal(0, builder.CurrentSize);
        Assert.Equal(0, builder.EmailCount);
        Assert.False(builder.ShouldFlush);

        // Create a test email
        var message = new MimeMessage();
        message.MessageId = "test@example.com";
        message.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.com"));
        message.Subject = "Test";
        message.Date = DateTime.UtcNow;
        
        var emailData = Encoding.UTF8.GetBytes("Test email content");
        var entry = builder.AddEmail(message, emailData);

        Assert.Equal(0, entry.LocalId);
        Assert.Equal(1, builder.EmailCount);
        Assert.Equal(emailData.Length, builder.CurrentSize);
        Assert.NotNull(entry.EnvelopeHash);
        Assert.NotNull(entry.ContentHash);
    }

    [Fact]
    public void ProtobufPayloadEncoding_SerializesAndDeserializes()
    {
        var encoding = new ProtobufPayloadEncoding();
        Assert.Equal(PayloadEncoding.Protobuf, encoding.EncodingType);

        var envelope = new EmailEnvelope
        {
            CompoundId = "123:0",
            MessageId = "test@example.com",
            Subject = "Test"
        };

        // Serialize
        var result = encoding.Serialize(envelope);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.Length > 0);

        // Deserialize
        var deserializeResult = encoding.Deserialize<EmailEnvelope>(result.Value);
        Assert.True(deserializeResult.IsSuccess);
        Assert.Equal("123:0", deserializeResult.Value.CompoundId);
        Assert.Equal("test@example.com", deserializeResult.Value.MessageId);
        Assert.Equal("Test", deserializeResult.Value.Subject);
    }

    [Fact]
    public void BlockFlags_CompressionAlgorithm_SetAndGet()
    {
        var flags = BlockFlags.None;
        
        // Test setting compression
        flags = flags.SetCompressionAlgorithm(CompressionAlgorithm.LZ4);
        Assert.True((flags & BlockFlags.Compressed) != 0);
        Assert.Equal(CompressionAlgorithm.LZ4, flags.GetCompressionAlgorithm());
        
        // Test clearing compression
        flags = flags.SetCompressionAlgorithm(CompressionAlgorithm.None);
        Assert.True((flags & BlockFlags.Compressed) == 0);
        Assert.Equal(CompressionAlgorithm.None, flags.GetCompressionAlgorithm());
    }

    [Fact]
    public void BlockFlags_EncryptionAlgorithm_SetAndGet()
    {
        var flags = BlockFlags.None;
        
        // Test setting encryption
        flags = flags.SetEncryptionAlgorithm(EncryptionAlgorithm.AES256_GCM);
        Assert.True((flags & BlockFlags.Encrypted) != 0);
        Assert.Equal(EncryptionAlgorithm.AES256_GCM, flags.GetEncryptionAlgorithm());
        
        // Test clearing encryption
        flags = flags.SetEncryptionAlgorithm(EncryptionAlgorithm.None);
        Assert.True((flags & BlockFlags.Encrypted) == 0);
        Assert.Equal(EncryptionAlgorithm.None, flags.GetEncryptionAlgorithm());
    }

    [Fact]
    public void ExtendedBlockHeader_SerializeDeserialize()
    {
        var header = new ExtendedBlockHeader
        {
            UncompressedSize = 1024,
            IV = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
            AuthTag = new byte[] { 17, 18, 19, 20 },
            KeyId = 42
        };

        // Serialize
        var data = header.Serialize();
        Assert.NotNull(data);
        Assert.True(data.Length > 0);

        // Deserialize
        var deserialized = ExtendedBlockHeader.Deserialize(data);
        Assert.Equal(1024, deserialized.UncompressedSize);
        Assert.Equal(16, deserialized.IV.Length);
        Assert.Equal(4, deserialized.AuthTag.Length);
        Assert.Equal(42, deserialized.KeyId);
    }

    [Fact]
    public void EmailBatchHashedID_CompoundKey()
    {
        var id = new EmailBatchHashedID
        {
            BlockId = 123,
            LocalId = 45
        };

        Assert.Equal("123:45", id.ToCompoundKey());

        var parsed = EmailBatchHashedID.FromCompoundKey("678:90");
        Assert.Equal(678, parsed.BlockId);
        Assert.Equal(90, parsed.LocalId);
    }

    [Fact]
    public void ZoneTreeSegmentContent_ImplementsBlockContent()
    {
        var kvContent = new ZoneTreeSegmentKVContent
        {
            SegmentId = "segment1",
            Version = 1,
            KeyValueData = new byte[] { 1, 2, 3 }
        };

        var vectorContent = new ZoneTreeSegmentVectorContent
        {
            SegmentId = "segment2",
            Version = 1,
            VectorData = new byte[] { 4, 5, 6 },
            IndexType = "HNSW"
        };

        Assert.IsAssignableFrom<BlockContent>(kvContent);
        Assert.IsAssignableFrom<BlockContent>(vectorContent);
        Assert.Equal(BlockType.ZoneTreeSegment_KV, kvContent.GetBlockType());
        Assert.Equal(BlockType.ZoneTreeSegment_Vector, vectorContent.GetBlockType());
    }

    [Fact]
    public void UpdatedBlockContents_InheritFromBlockContent()
    {
        var metadata = new MetadataContent();
        var header = new HeaderContent();
        var folder = new FolderContent();

        Assert.IsAssignableFrom<BlockContent>(metadata);
        Assert.IsAssignableFrom<BlockContent>(header);
        Assert.IsAssignableFrom<BlockContent>(folder);
        
        Assert.Equal(BlockType.Metadata, metadata.GetBlockType());
        Assert.Equal(BlockType.Metadata, header.GetBlockType()); // Headers use metadata type
        Assert.Equal(BlockType.Folder, folder.GetBlockType());
    }
}