using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeLookupTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeLookupTests()
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
    public async Task LookupAsync_ExistingKey_ReturnsCorrectLeafEntry()
    {
        // Arrange — insert a key with known values
        var filePath = Path.Combine(_tempDir, "test_lookup.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(10, 20, 30, 40);
        long expectedOffset = 4096;
        long expectedBlockId = 999;

        var insertResult = await btreeIndex.InsertAsync(key, expectedOffset, expectedBlockId);
        Assert.True(insertResult.IsSuccess, $"Insert failed: {insertResult.Error}");

        // Act
        var lookupResult = await btreeIndex.LookupAsync(key);

        // Assert — lookup succeeded
        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");

        // Assert — returned entry matches the inserted values
        var entry = lookupResult.Value;
        Assert.Equal(key, entry.Key);
        Assert.Equal(expectedOffset, entry.BlockOffset);
        Assert.Equal(expectedBlockId, entry.BlockId);
    }

    [Fact]
    public async Task LookupAsync_MultipleKeys_ReturnsCorrectEntryForEach()
    {
        // Arrange — insert several keys with distinct values
        var filePath = Path.Combine(_tempDir, "test_lookup_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var entries = new (EmailHashedID Key, long Offset, long BlockId)[]
        {
            (new EmailHashedID(1, 0, 0, 0), 100, 10),
            (new EmailHashedID(5, 0, 0, 0), 500, 50),
            (new EmailHashedID(10, 0, 0, 0), 1000, 100),
            (new EmailHashedID(20, 0, 0, 0), 2000, 200),
            (new EmailHashedID(50, 0, 0, 0), 5000, 500),
        };

        foreach (var (key, offset, blockId) in entries)
        {
            var r = await btreeIndex.InsertAsync(key, offset, blockId);
            Assert.True(r.IsSuccess, $"Insert failed for key {key}: {r.Error}");
        }

        // Act & Assert — each key returns the correct entry
        foreach (var (key, expectedOffset, expectedBlockId) in entries)
        {
            var lookupResult = await btreeIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess, $"Lookup failed for key {key}: {lookupResult.Error}");
            Assert.Equal(key, lookupResult.Value.Key);
            Assert.Equal(expectedOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(expectedBlockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task LookupAsync_EmptyTree_ReturnsNotFound()
    {
        // Arrange — create an empty tree (no inserts)
        var filePath = Path.Combine(_tempDir, "test_lookup_empty.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var missingKey = new EmailHashedID(99, 99, 99, 99);

        // Act
        var lookupResult = await btreeIndex.LookupAsync(missingKey);

        // Assert — result is a failure (not-found)
        Assert.True(lookupResult.IsFailure);
        Assert.Contains("empty", lookupResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LookupAsync_MissingKey_ReturnsNotFound()
    {
        // Arrange — insert some keys, then lookup one that was never inserted
        var filePath = Path.Combine(_tempDir, "test_lookup_missing.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var insertedKeys = new EmailHashedID[]
        {
            new EmailHashedID(1, 0, 0, 0),
            new EmailHashedID(5, 0, 0, 0),
            new EmailHashedID(10, 0, 0, 0),
        };

        foreach (var key in insertedKeys)
        {
            var r = await btreeIndex.InsertAsync(key, 100, 1);
            Assert.True(r.IsSuccess, $"Insert failed: {r.Error}");
        }

        var missingKey = new EmailHashedID(7, 0, 0, 0);

        // Act
        var lookupResult = await btreeIndex.LookupAsync(missingKey);

        // Assert — result is a failure (not-found)
        Assert.True(lookupResult.IsFailure);
        Assert.Contains("not found", lookupResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LookupAsync_MissingKeyInMultiLevelTree_ReturnsNotFound()
    {
        // Arrange — build a multi-level tree, then lookup a key that doesn't exist
        var filePath = Path.Combine(_tempDir, "test_lookup_missing_multilevel.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 1;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i * 2), 0, 0, 0); // even keys only
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        var missingKey = new EmailHashedID(3, 0, 0, 0); // odd key, never inserted

        // Act
        var lookupResult = await btreeIndex.LookupAsync(missingKey);

        // Assert — not-found in multi-level tree
        Assert.True(lookupResult.IsFailure);
        Assert.Contains("not found", lookupResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LookupAsync_AfterUpsert_ReturnsUpdatedValues()
    {
        // Arrange — insert then upsert the same key
        var filePath = Path.Combine(_tempDir, "test_lookup_upsert.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(42, 84, 126, 168);
        await btreeIndex.InsertAsync(key, 1000, 100);
        await btreeIndex.InsertAsync(key, 2000, 200); // upsert

        // Act
        var lookupResult = await btreeIndex.LookupAsync(key);

        // Assert — returns the updated values, not the originals
        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");
        Assert.Equal(2000, lookupResult.Value.BlockOffset);
        Assert.Equal(200, lookupResult.Value.BlockId);
    }

    [Fact]
    public async Task LookupAsync_InMultiLevelTree_ReturnsCorrectEntry()
    {
        // Arrange — build a tree with height >= 2 by inserting MaxEntries + 1 keys
        var filePath = Path.Combine(_tempDir, "test_lookup_multilevel.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 1;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Act & Assert — lookup a key that's guaranteed to be in the tree
        var targetKey = new EmailHashedID(10, 0, 0, 0);
        var lookupResult = await btreeIndex.LookupAsync(targetKey);

        Assert.True(lookupResult.IsSuccess, $"Lookup failed: {lookupResult.Error}");
        Assert.Equal(targetKey, lookupResult.Value.Key);
        Assert.Equal(9 * 100, lookupResult.Value.BlockOffset);  // key 10 was inserted at index 9
        Assert.Equal(9, lookupResult.Value.BlockId);
    }
}
