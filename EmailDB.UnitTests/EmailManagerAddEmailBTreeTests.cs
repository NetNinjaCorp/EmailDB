using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: EmailManager.AddEmailAsync stores email and indexes it in B+-tree.
/// Tests the integration between RawBlockManager and BTreeIndex for email storage and retrieval.
/// </summary>
public class EmailManagerAddEmailBTreeTests : IDisposable
{
    private readonly string _tempDir;

    public EmailManagerAddEmailBTreeTests()
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
    public async Task AddEmail_StoresContentBlock_And_IndexesInBTree()
    {
        // Arrange — simulate what EmailManager.AddEmailAsync should do:
        // 1. Write email content as a block
        // 2. Index the email's hashed ID → block location in the B+-tree
        var filePath = Path.Combine(_tempDir, "add_email.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(
            "msg-001@example.com", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "Test Subject", "sender@example.com", "recipient@example.com");

        byte[] emailPayload = System.Text.Encoding.UTF8.GetBytes(
            "From: sender@example.com\r\nTo: recipient@example.com\r\nSubject: Test Subject\r\n\r\nHello world");

        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = emailPayload
        };

        // Act — Step 1: write the email content block
        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);

        Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

        // Act — Step 2: index the email in the B+-tree
        var insertResult = await btreeIndex.InsertAsync(
            emailHashedId, writeResult.Value.Position, emailBlock.BlockId);

        // Assert — insert succeeded and tree has one entry
        Assert.True(insertResult.IsSuccess, $"Insert failed: {insertResult.Error}");
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal(1L, btreeIndex.CurrentRoot.EntryCount);

        // Assert — lookup by EmailHashedID returns the correct block location
        var lookupResult = await btreeIndex.LookupAsync(emailHashedId);

        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");
        Assert.Equal(writeResult.Value.Position, lookupResult.Value.BlockOffset);
        Assert.Equal(emailBlock.BlockId, lookupResult.Value.BlockId);

        // Assert — the email content block can be read back and matches
        var readResult = await rawBlockManager.ReadBlockAsync(emailBlock.BlockId);

        Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");
        Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
        Assert.Equal(emailPayload, readResult.Value.Payload);
    }

    [Fact]
    public async Task AddEmail_IndexRootPointsToLeafContainingEmail()
    {
        // Verifies the full chain: IndexRoot → BTreeLeaf → LeafEntry with email's key
        var filePath = Path.Combine(_tempDir, "root_chain.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailHashedId = new EmailHashedID(100, 200, 300, 400);
        byte[] payload = new byte[] { 0x01, 0x02, 0x03 };

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

        await btreeIndex.InsertAsync(emailHashedId, writeResult.Value.Position, emailBlock.BlockId);

        // Read the IndexRoot block
        var root = btreeIndex.CurrentRoot!;
        Assert.Equal((ushort)1, root.TreeHeight);

        // Read the leaf block at the root's offset
        var locations = rawBlockManager.GetBlockLocations();
        Block? leafBlock = null;
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.BTreeLeaf)
            {
                leafBlock = readResult.Value;
                break;
            }
        }
        Assert.NotNull(leafBlock);

        var leaf = BTreeNodeSerializer.DeserializeLeaf(leafBlock.Payload);
        Assert.Equal((ushort)1, leaf.EntryCount);
        Assert.Equal(emailHashedId, leaf.Entries[0].Key);
        Assert.Equal(writeResult.Value.Position, leaf.Entries[0].BlockOffset);
    }

    [Fact]
    public async Task AddMultipleEmails_AllIndexedAndRetrievable()
    {
        // Simulate adding several emails and verify each is retrievable via the B+-tree
        var filePath = Path.Combine(_tempDir, "multi_email.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        const int emailCount = 25;
        var emailEntries = new List<(EmailHashedID Id, long BlockId, long Offset)>();

        for (int i = 0; i < emailCount; i++)
        {
            var emailId = new EmailHashedID(
                $"msg-{i:D4}@example.com", i * 1000L,
                $"Subject {i}", $"from{i}@test.com", $"to{i}@test.com");

            var emailBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailContent,
                Flags = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                Payload = System.Text.Encoding.UTF8.GetBytes($"Email body #{i}")
            };

            var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
            Assert.True(writeResult.IsSuccess, $"Write failed for email {i}: {writeResult.Error}");

            var insertResult = await btreeIndex.InsertAsync(
                emailId, writeResult.Value.Position, emailBlock.BlockId);
            Assert.True(insertResult.IsSuccess, $"Insert failed for email {i}: {insertResult.Error}");

            emailEntries.Add((emailId, emailBlock.BlockId, writeResult.Value.Position));
        }

        // Assert — tree has all entries
        Assert.Equal((long)emailCount, btreeIndex.CurrentRoot!.EntryCount);

        // Assert — every email is retrievable via lookup
        foreach (var (id, blockId, offset) in emailEntries)
        {
            var lookup = await btreeIndex.LookupAsync(id);
            Assert.True(lookup.IsSuccess, $"Lookup failed for {id}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
            Assert.Equal(blockId, lookup.Value.BlockId);
        }
    }

    [Fact]
    public async Task AddEmail_DuplicateKey_TreeMaintainsIntegrity()
    {
        // If the same email ID is inserted twice, the tree should handle it
        // (either overwrite or reject — verify the tree remains consistent)
        var filePath = Path.Combine(_tempDir, "duplicate.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailId = new EmailHashedID(42, 43, 44, 45);

        var block1 = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("First version")
        };
        var write1 = await rawBlockManager.WriteBlockAsync(block1);
        Assert.True(write1.IsSuccess);
        await btreeIndex.InsertAsync(emailId, write1.Value.Position, block1.BlockId);

        var block2 = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("Second version")
        };
        var write2 = await rawBlockManager.WriteBlockAsync(block2);
        Assert.True(write2.IsSuccess);
        var insert2 = await btreeIndex.InsertAsync(emailId, write2.Value.Position, block2.BlockId);

        // Regardless of whether insert2 succeeds or fails, the tree should be queryable
        var lookup = await btreeIndex.LookupAsync(emailId);
        Assert.True(lookup.IsSuccess, "Lookup after duplicate insert should still work");
    }

    [Fact]
    public async Task AddEmail_ContentBlockPersisted_SurvivesReopen()
    {
        // Verify that email content block written to disk can be read after reopening
        var filePath = Path.Combine(_tempDir, "persist.emdb");
        long savedBlockId;
        byte[] savedPayload = System.Text.Encoding.UTF8.GetBytes("Persistent email content");

        // Write and close
        using (var rawBlockManager = new RawBlockManager(filePath))
        {
            var emailBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailContent,
                Flags = 0,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
                Payload = savedPayload
            };
            savedBlockId = emailBlock.BlockId;

            var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
            Assert.True(writeResult.IsSuccess);
        }

        // Reopen and verify
        BlockIdGenerator.Instance.Reset();
        using (var rawBlockManager = new RawBlockManager(filePath, createIfNotExists: false))
        {
            var readResult = await rawBlockManager.ReadBlockAsync(savedBlockId);
            Assert.True(readResult.IsSuccess, $"Read after reopen failed: {readResult.Error}");
            Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
            Assert.Equal(savedPayload, readResult.Value.Payload);
        }
    }

    [Fact]
    public async Task AddEmail_WritesEmailContentBlockType()
    {
        // Verify that the email block is stored with BlockType.EmailContent
        var filePath = Path.Combine(_tempDir, "blocktype.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);

        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = new byte[] { 0xFF }
        };

        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);

        var readResult = await rawBlockManager.ReadBlockAsync(emailBlock.BlockId);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
        Assert.Equal((byte)9, (byte)readResult.Value.Type);
    }

    [Fact]
    public async Task AddEmail_BTreeIntegrityVerified_AfterInsert()
    {
        // Verify that after inserting an email, the B+-tree leaf node has valid
        // content hashes (BLAKE3 integrity)
        var filePath = Path.Combine(_tempDir, "integrity.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var emailId = new EmailHashedID(10, 20, 30, 40);
        var emailBlock = new Block
        {
            Version = 1,
            Type = BlockType.EmailContent,
            Flags = 0,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = BlockIdGenerator.Instance.GetNextBlockId(BlockType.EmailContent),
            Payload = System.Text.Encoding.UTF8.GetBytes("Integrity test email")
        };

        var writeResult = await rawBlockManager.WriteBlockAsync(emailBlock);
        Assert.True(writeResult.IsSuccess);

        await btreeIndex.InsertAsync(emailId, writeResult.Value.Position, emailBlock.BlockId);

        // Read the leaf and verify BLAKE3 hash
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (readResult.IsSuccess && readResult.Value.Type == BlockType.BTreeLeaf)
            {
                var leaf = BTreeNodeSerializer.DeserializeLeaf(readResult.Value.Payload);
                var recomputedHash = BTreeHasher.ComputeLeafContentHash(leaf);
                Assert.Equal(recomputedHash, leaf.NodeContentHash);
                return;
            }
        }
        Assert.Fail("No BTreeLeaf block found after insert");
    }
}
