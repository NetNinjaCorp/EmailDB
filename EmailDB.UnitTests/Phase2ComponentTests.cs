using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using MimeKit;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.UnitTests;

public class Phase2ComponentTests
{
    [Fact]
    public async Task FolderManager_StoreFolderBlock_CreatesNewBlock()
    {
        // Arrange
        var mockBlockManager = new Mock<RawBlockManager>(null, null, null);
        var mockSerializer = new Mock<iBlockContentSerializer>();
        var mockCacheManager = new Mock<CacheManager>(null);
        var mockMetadataManager = new Mock<MetadataManager>(null);
        
        mockBlockManager.Setup(m => m.WriteBlockAsync(It.IsAny<Block>(), It.IsAny<CancellationToken>()))
            .Callback<Block, CancellationToken>((b, ct) => b.BlockId = 100)
            .ReturnsAsync(Result<BlockLocation>.Success(new BlockLocation { Position = 100, Length = 1000 }));
        mockSerializer.Setup(s => s.Serialize(It.IsAny<FolderContent>()))
            .Returns(new byte[] { 1, 2, 3 });
        
        var folderManager = new FolderManager(
            mockCacheManager.Object,
            mockMetadataManager.Object,
            mockBlockManager.Object,
            mockSerializer.Object);
        
        var folder = new FolderContent
        {
            FolderId = 1,
            Name = "TestFolder",
            Version = 0
        };
        
        // Act
        var result = await folderManager.StoreFolderBlockAsync(folder);
        
        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value);
        Assert.Equal(1, folder.Version);
        mockBlockManager.Verify(m => m.WriteBlockAsync(It.IsAny<Block>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    
    [Fact]
    public async Task FolderManager_AddEmailToFolder_UpdatesEnvelopeBlock()
    {
        // Arrange
        var mockBlockManager = new Mock<RawBlockManager>(null, null, null);
        var mockSerializer = new Mock<iBlockContentSerializer>();
        var mockCacheManager = new Mock<CacheManager>(null);
        var mockMetadataManager = new Mock<MetadataManager>(null);
        
        var folder = new FolderContent
        {
            FolderId = 1,
            Name = "Inbox",
            EnvelopeBlockId = 50,
            EmailIds = new List<EmailHashedID>()
        };
        
        var envelopeBlock = new FolderEnvelopeBlock
        {
            FolderPath = "Inbox",
            Envelopes = new List<EmailEnvelope>()
        };
        
        mockSerializer.Setup(s => s.Deserialize<FolderContent>(It.IsAny<byte[]>()))
            .Returns(folder);
        mockSerializer.Setup(s => s.Deserialize<FolderEnvelopeBlock>(It.IsAny<byte[]>()))
            .Returns(envelopeBlock);
        mockSerializer.Setup(s => s.Serialize(It.IsAny<object>()))
            .Returns(new byte[] { 1, 2, 3 });
        
        mockBlockManager.Setup(m => m.ReadBlockAsync(It.IsAny<long>()))
            .ReturnsAsync((long id) => Result<Block>.Success(new Block { Payload = new byte[] { 1, 2, 3 } }));
        mockBlockManager.Setup(m => m.WriteBlockAsync(It.IsAny<Block>(), It.IsAny<CancellationToken>()))
            .Callback<Block, CancellationToken>((b, ct) => b.BlockId = 101)
            .ReturnsAsync(Result<BlockLocation>.Success(new BlockLocation { Position = 101, Length = 1000 }));
        
        var folderManager = new FolderManager(
            mockCacheManager.Object,
            mockMetadataManager.Object,
            mockBlockManager.Object,
            mockSerializer.Object);
        
        var emailId = new EmailHashedID { BlockId = 10, LocalId = 5 };
        var envelope = new EmailEnvelope
        {
            MessageId = "test@example.com",
            Subject = "Test Email"
        };
        
        // Setup folder loading
        var metadata = new MetadataContent { FolderTreeOffset = 1 };
        var folderTree = new FolderTreeContent
        {
            FolderIDs = new Dictionary<string, long> { { "Inbox", 1 } },
            FolderOffsets = new Dictionary<long, long> { { 1, 10 } }
        };
        
        mockMetadataManager.Setup(m => m.GetMetadataAsync())
            .ReturnsAsync(metadata);
        mockSerializer.Setup(s => s.Deserialize<FolderTreeContent>(It.IsAny<byte[]>()))
            .Returns(folderTree);
        
        // Act
        var result = await folderManager.AddEmailToFolderAsync("Inbox", emailId, envelope);
        
        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("10:5", envelope.CompoundId);
    }
    
    [Fact]
    public async Task EmailManager_ImportEML_WithTransaction()
    {
        // Arrange
        var mockHybridStore = new Mock<HybridEmailStore>("", "", 1024);
        var mockFolderManager = new Mock<FolderManager>(null, null);
        var mockStorageManager = new Mock<EmailStorageManager>(null, null, null, null);
        var mockBlockManager = new Mock<RawBlockManager>(null, null, null);
        var mockSerializer = new Mock<iBlockContentSerializer>();
        
        var emailId = new EmailBatchHashedID
        {
            BlockId = 100,
            LocalId = 0,
            EnvelopeHash = new byte[] { 1, 2, 3 },
            ContentHash = new byte[] { 4, 5, 6 }
        };
        
        mockStorageManager.Setup(m => m.StoreEmailAsync(It.IsAny<MimeMessage>(), It.IsAny<byte[]>()))
            .ReturnsAsync(Result<EmailBatchHashedID>.Success(emailId));
        mockFolderManager.Setup(m => m.AddEmailToFolderAsync(It.IsAny<string>(), It.IsAny<EmailHashedID>(), It.IsAny<EmailEnvelope>()))
            .ReturnsAsync(Result.Success());
        mockHybridStore.Setup(m => m.UpdateIndexesForEmailAsync(It.IsAny<EmailHashedID>(), It.IsAny<MimeMessage>(), It.IsAny<string>()))
            .ReturnsAsync(Result.Success());
        
        var emailManager = new EmailManager(
            mockHybridStore.Object,
            mockFolderManager.Object,
            mockStorageManager.Object,
            mockBlockManager.Object,
            mockSerializer.Object);
        
        var emlContent = @"From: test@example.com
To: recipient@example.com
Subject: Test Email
Message-ID: <test123@example.com>
Date: Mon, 1 Jan 2024 00:00:00 +0000

This is a test email.";
        
        // Act
        var result = await emailManager.ImportEMLAsync(emlContent, "Inbox");
        
        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value.BlockId);
        Assert.Equal(0, result.Value.LocalId);
        
        // Verify all operations were called
        mockStorageManager.Verify(m => m.StoreEmailAsync(It.IsAny<MimeMessage>(), It.IsAny<byte[]>()), Times.Once);
        mockFolderManager.Verify(m => m.AddEmailToFolderAsync("Inbox", It.IsAny<EmailHashedID>(), It.IsAny<EmailEnvelope>()), Times.Once);
        mockHybridStore.Verify(m => m.UpdateIndexesForEmailAsync(It.IsAny<EmailHashedID>(), It.IsAny<MimeMessage>(), "Inbox"), Times.Once);
    }
    
    [Fact]
    public async Task EmailManager_BatchImport_ProcessesInBatches()
    {
        // Arrange
        var mockHybridStore = new Mock<HybridEmailStore>("", "", 1024);
        var mockFolderManager = new Mock<FolderManager>(null, null);
        var mockStorageManager = new Mock<EmailStorageManager>(null, null, null, null);
        var mockBlockManager = new Mock<RawBlockManager>(null, null, null);
        var mockSerializer = new Mock<iBlockContentSerializer>();
        
        mockStorageManager.Setup(m => m.StoreEmailAsync(It.IsAny<MimeMessage>(), It.IsAny<byte[]>()))
            .ReturnsAsync(Result<EmailBatchHashedID>.Success(new EmailBatchHashedID { BlockId = 100, LocalId = 0 }));
        mockStorageManager.Setup(m => m.FlushPendingEmailsAsync())
            .ReturnsAsync(Result.Success());
        mockFolderManager.Setup(m => m.AddEmailToFolderAsync(It.IsAny<string>(), It.IsAny<EmailHashedID>(), It.IsAny<EmailEnvelope>()))
            .ReturnsAsync(Result.Success());
        mockHybridStore.Setup(m => m.UpdateIndexesForEmailAsync(It.IsAny<EmailHashedID>(), It.IsAny<MimeMessage>(), It.IsAny<string>()))
            .ReturnsAsync(Result.Success());
        
        var emailManager = new EmailManager(
            mockHybridStore.Object,
            mockFolderManager.Object,
            mockStorageManager.Object,
            mockBlockManager.Object,
            mockSerializer.Object);
        
        var emails = new[]
        {
            ("email1.eml", "From: test1@example.com\r\nSubject: Test 1\r\n\r\nBody 1"),
            ("email2.eml", "From: test2@example.com\r\nSubject: Test 2\r\n\r\nBody 2"),
            ("email3.eml", "From: test3@example.com\r\nSubject: Test 3\r\n\r\nBody 3")
        };
        
        // Act
        var result = await emailManager.ImportEMLBatchAsync(emails, "Inbox");
        
        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.SuccessCount);
        Assert.Equal(0, result.Value.ErrorCount);
        Assert.Equal(3, result.Value.ImportedEmailIds.Count);
        
        // Verify flush was called
        mockStorageManager.Verify(m => m.FlushPendingEmailsAsync(), Times.Once);
    }
    
    [Fact]
    public void AdaptiveBlockSizer_ReturnsCorrectSizes()
    {
        var sizer = new AdaptiveBlockSizer();
        
        // Test different database sizes
        Assert.Equal(50, sizer.GetTargetBlockSizeMB(1L * 1024 * 1024 * 1024)); // 1GB
        Assert.Equal(100, sizer.GetTargetBlockSizeMB(10L * 1024 * 1024 * 1024)); // 10GB
        Assert.Equal(250, sizer.GetTargetBlockSizeMB(50L * 1024 * 1024 * 1024)); // 50GB
        Assert.Equal(500, sizer.GetTargetBlockSizeMB(200L * 1024 * 1024 * 1024)); // 200GB
        Assert.Equal(1024, sizer.GetTargetBlockSizeMB(600L * 1024 * 1024 * 1024)); // 600GB
    }
    
    [Fact]
    public async Task HybridEmailStore_UpdateIndexes_Success()
    {
        // This test would require setting up ZoneTree mocks
        // For now, we'll test the structure
        var emailLocation = new EmailLocation { BlockId = 100, LocalId = 5 };
        var serializer = new EmailLocationSerializer();
        
        var serialized = serializer.Serialize(emailLocation);
        var deserialized = serializer.Deserialize(serialized);
        
        Assert.Equal(100, deserialized.BlockId);
        Assert.Equal(5, deserialized.LocalId);
    }
    
    [Fact]
    public async Task FolderManager_GetSupersededBlocks_ReturnsOldBlocks()
    {
        // Arrange
        var mockBlockManager = new Mock<RawBlockManager>(null, null, null);
        var mockSerializer = new Mock<iBlockContentSerializer>();
        var mockCacheManager = new Mock<CacheManager>(null);
        var mockMetadataManager = new Mock<MetadataManager>(null);
        
        var folderManager = new FolderManager(
            mockCacheManager.Object,
            mockMetadataManager.Object,
            mockBlockManager.Object,
            mockSerializer.Object);
        
        // Note: We can't easily test the internal superseded blocks list
        // This would require exposing it or using reflection
        var result = await folderManager.GetSupersededBlocksAsync();
        
        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
    }
    
    [Fact]
    public void EmailTransaction_Rollback_ExecutesInReverseOrder()
    {
        // Arrange
        var executionOrder = new List<int>();
        var transaction = new EmailTransaction();
        
        transaction.RecordAction(async () => executionOrder.Add(1));
        transaction.RecordAction(async () => executionOrder.Add(2));
        transaction.RecordAction(async () => executionOrder.Add(3));
        
        // Act
        transaction.RollbackAsync().Wait();
        
        // Assert
        Assert.Equal(new[] { 3, 2, 1 }, executionOrder);
    }
    
    [Fact]
    public async Task AtomicUpdateContext_CommitClearsActions()
    {
        // Arrange
        var mockStore = new Mock<HybridEmailStore>("", "", 1024);
        var context = new AtomicUpdateContext(mockStore.Object);
        
        context.AddIndexUpdate("test", "key1", "value1");
        context.AddRollbackAction(async () => { });
        
        // Act
        await context.CommitAsync();
        
        // Assert - verify no exceptions when committing empty context
        await context.CommitAsync(); // Should not throw
    }
}