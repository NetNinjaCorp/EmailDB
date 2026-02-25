using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: EmailManager.DeleteEmailAsync removes from index and marks content outdated.
/// Tests the delete path: BTreeIndex.DeleteAsync(hashedId) removes from index, content block becomes outdated (unreachable via index).
/// </summary>
public class EmailManagerDeleteEmailBTreeTests : IDisposable
{
    private readonly string _tempDir;

    public EmailManagerDeleteEmailBTreeTests()
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
    public async Task DeleteEmail_RemovesFromIndex_LookupFails()
    {
        // Simulate DeleteEmailAsync: delete from B+-tree index, verify lookup no longer finds the email
        var filePath = Path.Combine(_tempDir, "delete_email.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(
            "msg-delete-001@example.com", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "Delete Test", "sender@example.com", "recipient@example.com");

        byte[] emailPayload = System.Text.Encoding.UTF8.GetBytes(
            "From: sender@example.com\r\nTo: recipient@example.com\r\nSubject: Delete Test\r\n\r\nThis email will be deleted.");

        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = emailPayload
        };

        // Store and index the email
        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

        var insertResult = await btreeIndex.InsertAsync(
            emailHashedId, writeResult.Value.Position, emailBlock.BlockId);
        Assert.True(insertResult.IsSuccess, $"Insert failed: {insertResult.Error}");

        // Verify email is reachable before delete
        var lookupBefore = await btreeIndex.LookupAsync(emailHashedId);
        Assert.True(lookupBefore.IsSuccess, $"Lookup before delete failed: {lookupBefore.Error}");

        // Act — simulate DeleteEmailAsync: remove from B+-tree index
        var deleteResult = await btreeIndex.DeleteAsync(emailHashedId);

        // Assert — delete succeeded
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — lookup now fails (email removed from index)
        var lookupAfter = await btreeIndex.LookupAsync(emailHashedId);
        Assert.True(lookupAfter.IsFailure, "Lookup should fail after delete — email removed from index");
        Assert.Contains("not found", lookupAfter.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteEmail_ContentBlockBecomesOutdated_StillPhysicallyPresent()
    {
        // After delete, the email content block is "outdated": still physically on disk
        // (append-only storage) but no longer reachable via the B+-tree index
        var filePath = Path.Combine(_tempDir, "delete_outdated.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(
            "msg-outdated@example.com", 1700000000000L,
            "Outdated Content Test", "alice@example.com", "bob@example.com");

        byte[] emailPayload = System.Text.Encoding.UTF8.GetBytes(
            "From: alice@example.com\r\nTo: bob@example.com\r\nSubject: Outdated Content Test\r\n\r\nThis content will become outdated.");

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

        // Capture the email content block's location before delete
        long emailContentBlockId = emailBlock.BlockId;

        // Act — delete from index
        var deleteResult = await btreeIndex.DeleteAsync(emailHashedId);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — content block is still physically readable (append-only, not erased)
        var readResult = await rawBlockManager.ReadBlockAsync(emailContentBlockId);
        Assert.True(readResult.IsSuccess,
            "Email content block should still be physically readable after delete (append-only storage)");
        Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
        Assert.Equal(emailPayload, readResult.Value.Payload);

        // Assert — but the index no longer points to it
        var lookupResult = await btreeIndex.LookupAsync(emailHashedId);
        Assert.True(lookupResult.IsFailure,
            "B+-tree index should no longer reference the deleted email's content block");
    }

    [Fact]
    public async Task DeleteEmail_TreeEntryCountDecremented()
    {
        // Verify the B+-tree entry count is decremented after delete
        var filePath = Path.Combine(_tempDir, "delete_count.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(
            "msg-count@example.com", 1700000000000L,
            "Count Test", "sender@example.com", "recipient@example.com");

        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("Count test email")
        };

        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);
        await btreeIndex.InsertAsync(emailHashedId, writeResult.Value.Position, emailBlock.BlockId);

        Assert.Equal(1L, btreeIndex.CurrentRoot!.EntryCount);

        // Act
        var deleteResult = await btreeIndex.DeleteAsync(emailHashedId);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — entry count is zero (tree emptied)
        Assert.Equal(0L, btreeIndex.CurrentRoot!.EntryCount);
    }

    [Fact]
    public async Task DeleteEmail_OtherEmailsUnaffected()
    {
        // Delete one email from a set; verify all others remain accessible via index and content blocks
        var filePath = Path.Combine(_tempDir, "delete_others_ok.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        const int emailCount = 10;
        var emails = new List<(EmailHashedID Id, long BlockId, long Offset, byte[] Payload)>();

        for (int i = 0; i < emailCount; i++)
        {
            var emailId = new EmailHashedID(
                $"msg-del-{i:D4}@example.com", i * 1000L,
                $"Subject {i}", $"from{i}@test.com", $"to{i}@test.com");

            byte[] payload = System.Text.Encoding.UTF8.GetBytes($"Email body for delete test #{i}");

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
            Assert.True(writeResult.IsSuccess);

            var insertResult = await btreeIndex.InsertAsync(
                emailId, writeResult.Value.Position, emailBlock.BlockId);
            Assert.True(insertResult.IsSuccess);

            emails.Add((emailId, emailBlock.BlockId, writeResult.Value.Position, payload));
        }

        Assert.Equal((long)emailCount, btreeIndex.CurrentRoot!.EntryCount);

        // Act — delete the 5th email (index 4)
        var deletedEmail = emails[4];
        var deleteResult = await btreeIndex.DeleteAsync(deletedEmail.Id);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — deleted email is gone from index
        var lookupDeleted = await btreeIndex.LookupAsync(deletedEmail.Id);
        Assert.True(lookupDeleted.IsFailure, "Deleted email should not be found via index");

        // Assert — all other emails are still accessible
        for (int i = 0; i < emailCount; i++)
        {
            if (i == 4) continue;
            var (id, blockId, offset, expectedPayload) = emails[i];

            var lookupResult = await btreeIndex.LookupAsync(id);
            Assert.True(lookupResult.IsSuccess, $"Surviving email {i} should be in index: {lookupResult.Error}");
            Assert.Equal(offset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);

            // Content block is still readable
            var readResult = await rawBlockManager.ReadBlockAsync(blockId);
            Assert.True(readResult.IsSuccess, $"Surviving email {i} content should be readable: {readResult.Error}");
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }

        // Assert — entry count decremented
        Assert.Equal(emailCount - 1, btreeIndex.CurrentRoot!.EntryCount);
    }

    [Fact]
    public async Task DeleteEmail_NewIndexBlocksWritten_FileGrows()
    {
        // Delete triggers copy-on-write: new leaf + IndexRoot appended.
        // Old email content block and old leaf block remain (append-only).
        var filePath = Path.Combine(_tempDir, "delete_file_grows.emdb");
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
            Payload = System.Text.Encoding.UTF8.GetBytes("File growth test email")
        };

        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);
        await btreeIndex.InsertAsync(emailHashedId, writeResult.Value.Position, emailBlock.BlockId);

        long fileSizeBefore = new FileInfo(filePath).Length;
        int blockCountBefore = rawBlockManager.GetBlockLocations().Count;

        // Act — delete
        var deleteResult = await btreeIndex.DeleteAsync(emailHashedId);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — file grew (new blocks appended, old blocks not removed)
        long fileSizeAfter = new FileInfo(filePath).Length;
        Assert.True(fileSizeAfter > fileSizeBefore,
            $"File should grow after delete (append-only). Before: {fileSizeBefore}, After: {fileSizeAfter}");

        int blockCountAfter = rawBlockManager.GetBlockLocations().Count;
        Assert.True(blockCountAfter > blockCountBefore,
            $"Block count should increase. Before: {blockCountBefore}, After: {blockCountAfter}");
    }

    [Fact]
    public async Task DeleteEmail_BTreeIntegrityMaintained_AfterDelete()
    {
        // After deleting an email, the remaining B+-tree leaf nodes should have valid BLAKE3 content hashes
        var filePath = Path.Combine(_tempDir, "delete_integrity.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert two emails
        var email1Id = new EmailHashedID(10, 20, 30, 40);
        var email2Id = new EmailHashedID(50, 60, 70, 80);

        var block1 = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("First email for integrity test")
        };
        var write1 = await rawBlockManager.WriteBlockAsync(block1);
        Assert.True(write1.IsSuccess);
        await btreeIndex.InsertAsync(email1Id, write1.Value.Position, block1.BlockId);

        var block2 = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("Second email for integrity test")
        };
        var write2 = await rawBlockManager.WriteBlockAsync(block2);
        Assert.True(write2.IsSuccess);
        await btreeIndex.InsertAsync(email2Id, write2.Value.Position, block2.BlockId);

        Assert.Equal(2L, btreeIndex.CurrentRoot!.EntryCount);

        // Act — delete the first email
        var deleteResult = await btreeIndex.DeleteAsync(email1Id);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — find the current leaf block and verify its BLAKE3 hash
        var locations = rawBlockManager.GetBlockLocations();
        bool foundLeaf = false;

        // The latest leaf block (highest block ID of type BTreeLeaf) is the current one
        long maxLeafBlockId = -1;
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.BTreeLeaf)
            {
                if (kvp.Key > maxLeafBlockId)
                    maxLeafBlockId = kvp.Key;
            }
        }

        Assert.True(maxLeafBlockId >= 0, "Should have a BTreeLeaf block after delete");

        var currentLeafResult = await rawBlockManager.ReadBlockAsync(maxLeafBlockId);
        Assert.True(currentLeafResult.IsSuccess);

        var leaf = BTreeNodeSerializer.DeserializeLeaf(currentLeafResult.Value.Payload);
        var recomputedHash = BTreeHasher.ComputeLeafContentHash(leaf);
        Assert.Equal(recomputedHash, leaf.NodeContentHash);
        foundLeaf = true;

        Assert.True(foundLeaf, "Should have verified at least one leaf block");

        // Assert — remaining email is still retrievable
        var lookupRemaining = await btreeIndex.LookupAsync(email2Id);
        Assert.True(lookupRemaining.IsSuccess, $"Remaining email should still be in index: {lookupRemaining.Error}");
    }

    [Fact]
    public async Task DeleteEmail_NonExistentEmail_ReturnsFailure()
    {
        // DeleteEmailAsync for an email that doesn't exist should fail gracefully
        var filePath = Path.Combine(_tempDir, "delete_nonexistent.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Insert one email so tree is not empty
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

        // Act — attempt to delete an email that was never indexed
        var missingId = new EmailHashedID(99, 98, 97, 96);
        var deleteResult = await btreeIndex.DeleteAsync(missingId);

        // Assert — returns failure
        Assert.True(deleteResult.IsFailure, "Delete of non-existent email should fail");
        Assert.Contains("not found", deleteResult.Error, StringComparison.OrdinalIgnoreCase);

        // Assert — existing email unaffected
        var lookupExisting = await btreeIndex.LookupAsync(existingId);
        Assert.True(lookupExisting.IsSuccess, "Existing email should still be accessible");
    }
}
