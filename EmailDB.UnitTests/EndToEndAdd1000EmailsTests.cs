using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-end test: add 1000 emails then retrieve each by ID.
/// Verifies acceptance criterion for story US-EMDB-33.
/// </summary>
public class EndToEndAdd1000EmailsTests : IDisposable
{
    private readonly string _tempDir;

    public EndToEndAdd1000EmailsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Add1000Emails_ThenRetrieveEachById()
    {
        const int emailCount = 1000;
        var filePath = Path.Combine(_tempDir, "e2e_1000.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var stored = new List<(EmailHashedID Id, long BlockId, long Offset, byte[] Payload)>(emailCount);

        // Phase 1: Add 1000 emails
        for (int i = 0; i < emailCount; i++)
        {
            var emailId = new EmailHashedID(
                $"msg-{i:D4}@e2e-test.com", i * 1000L,
                $"E2E Subject {i}", $"sender{i}@test.com", $"recipient{i}@test.com");

            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                $"From: sender{i}@test.com\r\nTo: recipient{i}@test.com\r\nSubject: E2E Subject {i}\r\n\r\nEmail body #{i}");

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

        // Verify tree entry count
        Assert.NotNull(btreeIndex.CurrentRoot);
        Assert.Equal((long)emailCount, btreeIndex.CurrentRoot.EntryCount);

        // Phase 2: Retrieve each email by ID and verify
        for (int i = 0; i < stored.Count; i++)
        {
            var (id, blockId, offset, expectedPayload) = stored[i];

            // Lookup via B+-tree
            var lookupResult = await btreeIndex.LookupAsync(id);
            Assert.True(lookupResult.IsSuccess, $"Lookup failed for email {i}: {lookupResult.Error}");
            Assert.Equal(offset, lookupResult.Value.BlockOffset);
            Assert.Equal(blockId, lookupResult.Value.BlockId);

            // Read back the content block
            var readResult = await rawBlockManager.ReadBlockAsync(lookupResult.Value.BlockId);
            Assert.True(readResult.IsSuccess, $"Read failed for email {i}: {readResult.Error}");
            Assert.Equal(BlockType.EmailContent, readResult.Value.Type);
            Assert.Equal(expectedPayload, readResult.Value.Payload);
        }
    }
}
