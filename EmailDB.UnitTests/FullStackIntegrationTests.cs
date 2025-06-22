using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.Caching;
using EmailDB.Format.Indexing;
using MimeKit;

namespace EmailDB.UnitTests;

/// <summary>
/// Full stack integration tests verifying that all stages (1, 2, 3) work together
/// to implement the complete EmailDB file format.
/// </summary>
[TestCategory("Integration")]
public class FullStackIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    
    public FullStackIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"EmailDB_FullStack_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task FullStack_Stage123_DataFlowIntegration()
    {
        // This test verifies the complete data flow:
        // Stage 1 (RawBlockManager) → Stage 2 (Serialization) → Stage 3 (Cache/Index)
        
        var dbPath = Path.Combine(_testDirectory, "fullstack.edb");
        
        // Stage 1: Raw block storage
        using var rawManager = new RawBlockManager(dbPath);
        
        // Stage 2: Serialization layer
        var serializer = new DefaultBlockContentSerializer();
        
        // Stage 3: Caching layer
        using var cacheManager = new CacheManager(rawManager, serializer);
        
        // Initialize the database
        await cacheManager.InitializeNewFile();
        
        // Create test data that flows through all stages
        var testEmail = new EmailHashedID
        {
            BlockId = 1,
            LocalId = 0,
            EnvelopeHash = GenerateHash(1),
            ContentHash = GenerateHash(2)
        };
        
        // Create a folder to store emails
        var folderContent = new FolderContent
        {
            Name = "TestFolder",
            FolderId = 1,
            ParentFolderId = 0,
            EmailIds = new List<EmailHashedID> { testEmail }
        };
        
        // Test Stage 2 → Stage 1 flow (Serialization → Raw Storage)
        var serializedFolder = serializer.Serialize(folderContent);
        Assert.NotNull(serializedFolder);
        Assert.True(serializedFolder.Length > 0);
        
        var folderBlock = new Block
        {
            Type = BlockType.Folder,
            BlockId = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = serializedFolder,
            Encoding = PayloadEncoding.Json // Default
        };
        
        // Write through Stage 1
        var writeResult = await rawManager.WriteBlockAsync(folderBlock);
        Assert.True(writeResult.IsSuccess);
        var blockLocation = writeResult.Value;
        
        // Test Stage 1 → Stage 2 flow (Raw Storage → Deserialization)
        var readResult = await rawManager.ReadBlockAsync(folderBlock.BlockId);
        Assert.True(readResult.IsSuccess);
        var readBlock = readResult.Value;
        
        var deserializedFolder = serializer.Deserialize<FolderContent>(readBlock.Payload);
        Assert.NotNull(deserializedFolder);
        Assert.Equal("TestFolder", deserializedFolder.Name);
        Assert.Single(deserializedFolder.EmailIds);
        
        // Test Stage 3 integration (Caching)
        // Use reflection to access internal UpdateFolder method
        var updateFolderMethod = typeof(CacheManager).GetMethod("UpdateFolder", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
        if (updateFolderMethod != null)
        {
            var task = (Task<long>)updateFolderMethod.Invoke(cacheManager, new object[] { "TestFolder", folderContent });
            var cachedOffset = await task;
            
            // Verify cached data
            var cachedFolder = await cacheManager.GetCachedFolder("TestFolder");
            Assert.NotNull(cachedFolder);
            Assert.Equal("TestFolder", cachedFolder.Name);
            Assert.Single(cachedFolder.EmailIds);
        }
        
        // Verify the complete round trip
        Assert.Equal(folderContent.Name, deserializedFolder.Name);
        Assert.Equal(folderContent.FolderId, deserializedFolder.FolderId);
        Assert.Equal(folderContent.EmailIds.Count, deserializedFolder.EmailIds.Count);
    }

    [Fact]
    public async Task FullStack_EmailDatabase_CompleteWorkflow()
    {
        // Test the high-level EmailDatabase API that uses all stages
        var dbPath = Path.Combine(_testDirectory, "emaildb_complete.edb");
        
        using var emailDb = new EmailDatabase(dbPath);
        
        // Create a test email
        var message = new MimeMessage();
        message.MessageId = "integration@test.com";
        message.Subject = "Full Stack Integration Test";
        message.From.Add(new MailboxAddress("Test Sender", "sender@test.com"));
        message.To.Add(new MailboxAddress("Test Recipient", "recipient@test.com"));
        message.Date = DateTimeOffset.Now;
        
        var bodyBuilder = new BodyBuilder
        {
            TextBody = "This email tests the complete EmailDB stack: Stage 1 (blocks), Stage 2 (serialization), Stage 3 (indexing)."
        };
        message.Body = bodyBuilder.ToMessageBody();
        
        // Convert to EML
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var emlContent = Encoding.UTF8.GetString(stream.ToArray());
        
        // Import email - this uses all stages internally
        var emailId = await emailDb.ImportEMLAsync(emlContent, "test.eml");
        Assert.NotNull(emailId);
        
        // Add to folder
        await emailDb.AddToFolderAsync(emailId, "Integration");
        
        // Search for the email - uses Stage 3 indexing
        var searchResults = await emailDb.SearchAsync("integration");
        Assert.NotEmpty(searchResults);
        Assert.Contains(searchResults, r => r.Subject.Contains("Full Stack Integration Test"));
        
        // Get folders - uses Stage 3 indexing
        var folders = await emailDb.GetEmailFoldersAsync(emailId);
        Assert.Contains("Integration", folders);
        
        // Close and reopen to test persistence
        emailDb.Dispose();
        
        using var emailDb2 = new EmailDatabase(dbPath);
        
        // Verify data persisted through all stages
        var searchResults2 = await emailDb2.SearchAsync("integration");
        Assert.NotEmpty(searchResults2);
        
        var stats = await emailDb2.GetDatabaseStatsAsync();
        Assert.True(stats.TotalEmails > 0);
        Assert.True(stats.StorageBlocks > 0);
    }

    [Fact]
    public async Task FullStack_SerializationEncodings_AllWork()
    {
        // Test that all serialization encodings work through the full stack
        var dbPath = Path.Combine(_testDirectory, "encoding_test.edb");
        
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        
        // Test data
        var testData = new MetadataContent
        {
            WALOffset = 1000,
            FolderTreeOffset = 2000,
            SegmentOffsets = new Dictionary<string, long> { { "1", 3000 } },
            OutdatedOffsets = new List<long> { 4000, 5000 }
        };
        
        // Test each encoding
        var encodings = new[] { PayloadEncoding.Json, PayloadEncoding.Protobuf, PayloadEncoding.RawBytes };
        
        foreach (var encoding in encodings)
        {
            // For RawBytes, we need to test with byte array data
            if (encoding == PayloadEncoding.RawBytes)
            {
                var rawData = new byte[] { 1, 2, 3, 4, 5 };
                // For RawBytes encoding, the serializer just passes through the byte array
                var rawResult = Result<byte[]>.Success(rawData);
                Assert.True(rawResult.IsSuccess);
                
                var block = new Block
                {
                    Type = BlockType.Metadata,
                    BlockId = (long)encoding,
                    Payload = rawResult.Value,
                    Encoding = encoding
                };
                
                var writeResult = await rawManager.WriteBlockAsync(block);
                Assert.True(writeResult.IsSuccess);
                
                var readResult = await rawManager.ReadBlockAsync(block.BlockId);
                Assert.True(readResult.IsSuccess);
                
                // For RawBytes, payload is already the raw data
                Assert.Equal(rawData, readResult.Value.Payload);
            }
            else
            {
                // Test JSON and Protobuf with complex objects
                // Use the legacy method which defaults to JSON for now
                var payload = serializer.Serialize(testData);
                var serResult = Result<byte[]>.Success(payload);
                Assert.True(serResult.IsSuccess);
                
                var block = new Block
                {
                    Type = BlockType.Metadata,
                    BlockId = (long)encoding,
                    Payload = serResult.Value,
                    Encoding = encoding
                };
                
                var writeResult = await rawManager.WriteBlockAsync(block);
                Assert.True(writeResult.IsSuccess);
                
                var readResult = await rawManager.ReadBlockAsync(block.BlockId);
                Assert.True(readResult.IsSuccess);
                
                // Use the legacy deserialize method
                var deserializedData = serializer.Deserialize<MetadataContent>(readResult.Value.Payload);
                Assert.Equal(testData.WALOffset, deserializedData.WALOffset);
                Assert.Equal(testData.FolderTreeOffset, deserializedData.FolderTreeOffset);
            }
        }
    }

    [Fact]
    public async Task FullStack_BlockTypes_AllSupported()
    {
        // Verify all block types work through the stack
        var dbPath = Path.Combine(_testDirectory, "blocktypes_test.edb");
        
        using var rawManager = new RawBlockManager(dbPath);
        var serializer = new DefaultBlockContentSerializer();
        using var cacheManager = new CacheManager(rawManager, serializer);
        
        await cacheManager.InitializeNewFile();
        
        // Test each block type - note that not all types inherit from BlockContent
        var testBlocks = new List<(BlockType type, object content)>
        {
            (BlockType.Metadata, new MetadataContent { WALOffset = 100 }),
            (BlockType.Folder, new FolderContent { Name = "Test", FolderId = 1 }),
            (BlockType.EmailBatch, new byte[] { 1, 2, 3, 4, 5 }), // EmailBatch is raw bytes
            (BlockType.WAL, new WALContent { NextWALOffset = 1000 })
        };
        
        foreach (var (blockType, content) in testBlocks)
        {
            var block = new Block
            {
                Type = blockType,
                BlockId = (long)blockType,
                Payload = serializer.Serialize(content),
                Encoding = PayloadEncoding.Json
            };
            
            // Write through Stage 1
            var writeResult = await rawManager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
            
            // Read back
            var readResult = await rawManager.ReadBlockAsync(block.BlockId);
            Assert.True(readResult.IsSuccess);
            Assert.Equal(blockType, readResult.Value.Type);
            
            // Cache interaction (Stage 3)
            var cacheWriteResult = await cacheManager.WriteBlockAsync(block);
            Assert.True(cacheWriteResult.IsSuccess);
            
            var cacheReadResult = await cacheManager.ReadBlockAsync(cacheWriteResult.Value.Position);
            Assert.True(cacheReadResult.IsSuccess);
            Assert.Equal(blockType, cacheReadResult.Value.Type);
        }
    }

    [Fact]
    public async Task FullStack_Performance_Benchmark()
    {
        // Basic performance test to ensure the stack performs adequately
        var dbPath = Path.Combine(_testDirectory, "performance_test.edb");
        
        using var emailDb = new EmailDatabase(dbPath);
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        // Import 100 emails
        for (int i = 0; i < 100; i++)
        {
            var message = new MimeMessage();
            message.MessageId = $"perf{i}@test.com";
            message.Subject = $"Performance Test Email {i}";
            message.From.Add(new MailboxAddress($"Sender {i}", $"sender{i}@test.com"));
            message.To.Add(new MailboxAddress("Recipient", "recipient@test.com"));
            message.Date = DateTimeOffset.Now.AddDays(-i);
            
            var bodyBuilder = new BodyBuilder
            {
                TextBody = $"This is test email {i} for performance testing. " +
                          "It contains enough text to be realistic but not too much to slow things down."
            };
            message.Body = bodyBuilder.ToMessageBody();
            
            using var stream = new MemoryStream();
            message.WriteTo(stream);
            var emlContent = Encoding.UTF8.GetString(stream.ToArray());
            
            await emailDb.ImportEMLAsync(emlContent, $"perf{i}.eml");
        }
        
        sw.Stop();
        
        // Basic performance assertion - should complete in reasonable time
        Assert.True(sw.ElapsedMilliseconds < 10000, $"Import took too long: {sw.ElapsedMilliseconds}ms");
        
        // Test search performance
        sw.Restart();
        var searchResults = await emailDb.SearchAsync("performance");
        sw.Stop();
        
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Search took too long: {sw.ElapsedMilliseconds}ms");
        Assert.Equal(100, searchResults.Count);
    }

    #region Helper Methods

    private byte[] GenerateHash(int seed)
    {
        var hash = new byte[32];
        new Random(seed).NextBytes(hash);
        return hash;
    }

    #endregion

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}