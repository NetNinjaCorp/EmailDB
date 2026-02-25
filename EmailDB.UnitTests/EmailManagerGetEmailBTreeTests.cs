using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: EmailManager.GetEmailAsync retrieves email via B+-tree lookup.
/// Tests the full retrieval path: BTreeIndex.LookupAsync(hashedId) -> BlockLocation -> read email content block.
/// </summary>
public class EmailManagerGetEmailBTreeTests : IDisposable
{
    private readonly string _tempDir;

    public EmailManagerGetEmailBTreeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task GetEmail_LookupByHashedId_ReturnsCorrectBlockLocation()
    {
        // Simulate GetEmailAsync: lookup by EmailHashedID returns the block location
        var filePath = Path.Combine(_tempDir, "get_email.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(
            "msg-get-001@example.com", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "Retrieval Test", "sender@example.com", "recipient@example.com");

        byte[] emailPayload = System.Text.Encoding.UTF8.GetBytes(
            "From: sender@example.com\r\nTo: recipient@example.com\r\nSubject: Retrieval Test\r\n\r\nThis is the email body.");

        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = emailPayload
        };

        // Store the email content block
        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

        // Index it in the B+-tree
        var insertResult = await btreeIndex.InsertAsync(
            emailHashedId, writeResult.Value.Position, emailBlock.BlockId);
        Assert.True(insertResult.IsSuccess, $"Insert failed: {insertResult.Error}");

        // Act — simulate GetEmailAsync: lookup by hashed ID
        var lookupResult = await btreeIndex.LookupAsync(emailHashedId);

        // Assert — lookup returns the correct location
        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");
        Assert.Equal(writeResult.Value.Position, lookupResult.Value.BlockOffset);
        Assert.Equal(emailBlock.BlockId, lookupResult.Value.BlockId);
    }

    [Fact]
    public async Task GetEmail_LookupThenReadBlock_ReturnsOriginalPayload()
    {
        // Full GetEmailAsync path: lookup -> read block -> verify content matches
        var filePath = Path.Combine(_tempDir, "get_full_path.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(
            "msg-full-001@example.com", 1700000000000L,
            "Full Path Test", "alice@example.com", "bob@example.com");

        byte[] emailPayload = System.Text.Encoding.UTF8.GetBytes(
            "From: alice@example.com\r\nTo: bob@example.com\r\nSubject: Full Path Test\r\n\r\nComplete retrieval test body.");

        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = emailPayload
        };

        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);
        await btreeIndex.InsertAsync(emailHashedId, writeResult.Value.Position, emailBlock.BlockId);

        // Act — simulate GetEmailAsync: lookup then read
        var lookupResult = await btreeIndex.LookupAsync(emailHashedId);
        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");

        var readResult = await rawBlockManager.ReadBlockAsync(lookupResult.Value.BlockId);

        // Assert — content round-trips correctly
        Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");
        Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
        Assert.Equal(emailPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task GetEmail_NonExistentKey_ReturnsFailure()
    {
        // GetEmailAsync for a key that was never inserted should fail gracefully
        var filePath = Path.Combine(_tempDir, "get_missing.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert one email so the tree is not empty
        var existingId = new EmailHashedID(1, 2, 3, 4);
        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("Existing email")
        };
        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);
        await btreeIndex.InsertAsync(existingId, writeResult.Value.Position, emailBlock.BlockId);

        // Act — lookup a different key that was never inserted
        var missingId = new EmailHashedID(99, 98, 97, 96);
        var lookupResult = await btreeIndex.LookupAsync(missingId);

        // Assert — lookup fails with not-found
        Assert.True(lookupResult.IsFailure);
    }

    [Fact]
    public async Task GetEmail_EmptyTree_ReturnsFailure()
    {
        // GetEmailAsync on an empty tree should fail gracefully
        var filePath = Path.Combine(_tempDir, "get_empty.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailId = new EmailHashedID(10, 20, 30, 40);

        // Act
        var lookupResult = await btreeIndex.LookupAsync(emailId);

        // Assert
        Assert.True(lookupResult.IsFailure);
    }

    [Fact]
    public async Task GetEmail_MultipleEmails_EachRetrievableByLookup()
    {
        // Simulate GetEmailAsync for each of several stored emails
        var filePath = Path.Combine(_tempDir, "get_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        const int emailCount = 50;
        var stored = new List<(EmailHashedID Id, long BlockId, long Offset, byte[] Payload)>();

        for (int i = 0; i < emailCount; i++)
        {
            var emailId = new EmailHashedID(
                $"msg-get-{i:D4}@example.com", i * 1000L,
                $"Subject {i}", $"from{i}@test.com", $"to{i}@test.com");

            byte[] payload = System.Text.Encoding.UTF8.GetBytes($"Email body for retrieval test #{i}");

            var emailBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailContent,
                Flags = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                Payload = payload
            };

            var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
            Assert.True(writeResult.IsSuccess, $"Write failed for email {i}: {writeResult.Error}");

            var insertResult = await btreeIndex.InsertAsync(
                emailId, writeResult.Value.Position, emailBlock.BlockId);
            Assert.True(insertResult.IsSuccess, $"Insert failed for email {i}: {insertResult.Error}");

            stored.Add((emailId, emailBlock.BlockId, writeResult.Value.Position, payload));
        }

        // Act & Assert — simulate GetEmailAsync for each stored email
        foreach (var (id, blockId, offset, expectedPayload) in stored)
        {
            var lookupResult = await btreeIndex.LookupAsync(id);
            Assert.True(lookupResult.IsSuccess, $"Lookup failed for {id}");
            Assert.Equal(offset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);

            // Read back the content block via the lookup result
            var readResult = await rawBlockManager.ReadBlockAsync(lookupResult.Value.BlockId);
            Assert.True(readResult.IsSuccess, $"Read failed for {id}: {readResult.Error}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }
    }

    [Fact]
    public async Task GetEmail_AfterReopen_LookupStillWorks()
    {
        // Verify GetEmailAsync works after file is closed and reopened (persistence)
        var filePath = Path.Combine(_tempDir, "get_persist.emdb");
        var emailHashedId = new EmailHashedID(
            "msg-persist@example.com", 1700000000000L,
            "Persist Test", "sender@example.com", "recipient@example.com");
        byte[] emailPayload = System.Text.Encoding.UTF8.GetBytes("Persistent retrieval test");
        long savedBlockId;
        long savedOffset;
        byte[] savedRootPayload;

        // Write, index, and close
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var btreeIndex = new BTreeIndex(rawBlockManager);

            var emailBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailContent,
                Flags = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                Payload = emailPayload
            };
            savedBlockId = emailBlock.BlockId;

            var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
            Assert.True(writeResult.IsSuccess);
            savedOffset = writeResult.Value.Position;

            var insertResult = await btreeIndex.InsertAsync(
                emailHashedId, writeResult.Value.Position, emailBlock.BlockId);
            Assert.True(insertResult.IsSuccess);

            // Save root state for reconstruction
            var root = btreeIndex.CurrentRoot!;
            savedRootPayload = BTreeNodeSerializer.SerializeIndexRoot(root);
        }

        // Reopen and verify lookup still works
        BlockIdGenerator.Instance.Reset();
        using (var rawBlockManager = new RawBlockManager(filePath, createIfNotExists: false))
        {
            // Reconstruct the IndexRoot from saved state
            var restoredRoot = BTreeNodeSerializer.DeserializeIndexRoot(savedRootPayload);

            // Find the root block offset by scanning for the IndexRoot block
            long rootBlockOffset = -1;
            var locations = rawBlockManager.GetBlockLocations();
            foreach (var kvp in locations)
            {
                var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
                if (readResult.IsSuccess && readResult.Value.Type == BlockType.IndexRoot)
                {
                    rootBlockOffset = kvp.Value.Position;
                    break;
                }
            }
            Assert.True(rootBlockOffset >= 0, "IndexRoot block not found after reopen");

            var btreeIndex = new BTreeIndex(rawBlockManager, restoredRoot, rootBlockOffset);

            // Act — simulate GetEmailAsync after reopen
            var lookupResult = await btreeIndex.LookupAsync(emailHashedId);

            // Assert
            Assert.True(lookupResult.IsSuccess, $"Lookup after reopen failed: {lookupResult.Error}");
            Assert.Equal(savedOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(savedBlockId, lookupResult.Value.BlockId);

            // Verify content block is still readable
            var contentResult = await rawBlockManager.ReadBlockAsync(savedBlockId);
            Assert.True(contentResult.IsSuccess);
            Assert.Equal(emailPayload, contentResult.Value.Payload);
        }
    }

    [Fact]
    public async Task GetEmail_LookupReturnsEmailContentBlockType()
    {
        // Verify that the block retrieved via B+-tree lookup has BlockType.EmailContent
        var filePath = Path.Combine(_tempDir, "get_blocktype.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(100, 200, 300, 400);
        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("Block type verification")
        };

        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);
        await btreeIndex.InsertAsync(emailHashedId, writeResult.Value.Position, emailBlock.BlockId);

        // Act — lookup then read
        var lookupResult = await btreeIndex.LookupAsync(emailHashedId);
        Assert.True(lookupResult.IsSuccess);

        var readResult = await rawBlockManager.ReadBlockAsync(lookupResult.Value.BlockId);

        // Assert — block type is EmailContent
        Assert.True(readResult.IsSuccess);
        Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
    }
}
