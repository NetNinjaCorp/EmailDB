using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeRangeQueryTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeRangeQueryTests()
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
    public async Task RangeQueryAsync_ReturnsAllEntriesInSortedOrderBetweenStartAndEndKeys()
    {
        // Arrange — insert 10 keys with known sort order
        var filePath = Path.Combine(_tempDir, "test_range_sorted.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var allKeys = new (EmailHashedID Key, long Offset, long BlockId)[]
        {
            (new EmailHashedID(1, 0, 0, 0), 100, 1),
            (new EmailHashedID(3, 0, 0, 0), 300, 3),
            (new EmailHashedID(5, 0, 0, 0), 500, 5),
            (new EmailHashedID(7, 0, 0, 0), 700, 7),
            (new EmailHashedID(9, 0, 0, 0), 900, 9),
            (new EmailHashedID(11, 0, 0, 0), 1100, 11),
            (new EmailHashedID(13, 0, 0, 0), 1300, 13),
            (new EmailHashedID(15, 0, 0, 0), 1500, 15),
            (new EmailHashedID(17, 0, 0, 0), 1700, 17),
            (new EmailHashedID(19, 0, 0, 0), 1900, 19),
        };

        foreach (var (key, offset, blockId) in allKeys)
        {
            var r = await btreeIndex.InsertAsync(key, offset, blockId);
            Assert.True(r.IsSuccess, $"Insert failed for key {key}: {r.Error}");
        }

        var startKey = new EmailHashedID(5, 0, 0, 0);
        var endKey = new EmailHashedID(15, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert — result is successful
        Assert.True(rangeResult.IsSuccess, $"Range query failed: {rangeResult.Error}");

        var entries = rangeResult.Value;

        // Assert — returns exactly the entries in [5, 15]
        Assert.Equal(6, entries.Count); // keys 5, 7, 9, 11, 13, 15

        // Assert — entries are in sorted order
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i - 1].Key.CompareTo(entries[i].Key) < 0,
                $"Entry at index {i - 1} ({entries[i - 1].Key}) should be less than entry at index {i} ({entries[i].Key})");
        }

        // Assert — expected keys and values
        Assert.Equal(new EmailHashedID(5, 0, 0, 0), entries[0].Key);
        Assert.Equal(500, entries[0].BlockOffset);
        Assert.Equal(new EmailHashedID(7, 0, 0, 0), entries[1].Key);
        Assert.Equal(new EmailHashedID(9, 0, 0, 0), entries[2].Key);
        Assert.Equal(new EmailHashedID(11, 0, 0, 0), entries[3].Key);
        Assert.Equal(new EmailHashedID(13, 0, 0, 0), entries[4].Key);
        Assert.Equal(new EmailHashedID(15, 0, 0, 0), entries[5].Key);
        Assert.Equal(1500, entries[5].BlockOffset);
    }

    [Fact]
    public async Task RangeQueryAsync_EmptyTree_ReturnsEmptyList()
    {
        // Arrange — empty tree
        var filePath = Path.Combine(_tempDir, "test_range_empty.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var startKey = new EmailHashedID(1, 0, 0, 0);
        var endKey = new EmailHashedID(100, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert — empty result, not a failure
        Assert.True(rangeResult.IsSuccess);
        Assert.Empty(rangeResult.Value);
    }

    [Fact]
    public async Task RangeQueryAsync_StartKeyGreaterThanEndKey_ReturnsFailure()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test_range_invalid.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        await btreeIndex.InsertAsync(new EmailHashedID(5, 0, 0, 0), 500, 5);

        var startKey = new EmailHashedID(10, 0, 0, 0);
        var endKey = new EmailHashedID(1, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert
        Assert.True(rangeResult.IsFailure);
    }

    [Fact]
    public async Task RangeQueryAsync_NoKeysInRange_ReturnsEmptyList()
    {
        // Arrange — all keys outside the query range
        var filePath = Path.Combine(_tempDir, "test_range_nomatch.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        await btreeIndex.InsertAsync(new EmailHashedID(2, 0, 0, 0), 200, 2);
        await btreeIndex.InsertAsync(new EmailHashedID(3, 0, 0, 0), 300, 3);

        var startKey = new EmailHashedID(10, 0, 0, 0);
        var endKey = new EmailHashedID(20, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert
        Assert.True(rangeResult.IsSuccess);
        Assert.Empty(rangeResult.Value);
    }

    [Fact]
    public async Task RangeQueryAsync_ExactBoundaryKeys_IncludesBothEndpoints()
    {
        // Arrange — ensure start and end keys are included (inclusive range)
        var filePath = Path.Combine(_tempDir, "test_range_boundaries.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        await btreeIndex.InsertAsync(new EmailHashedID(5, 0, 0, 0), 500, 5);
        await btreeIndex.InsertAsync(new EmailHashedID(10, 0, 0, 0), 1000, 10);
        await btreeIndex.InsertAsync(new EmailHashedID(15, 0, 0, 0), 1500, 15);

        var startKey = new EmailHashedID(5, 0, 0, 0);
        var endKey = new EmailHashedID(15, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert — all 3 keys included
        Assert.True(rangeResult.IsSuccess);
        Assert.Equal(3, rangeResult.Value.Count);
        Assert.Equal(new EmailHashedID(5, 0, 0, 0), rangeResult.Value[0].Key);
        Assert.Equal(new EmailHashedID(15, 0, 0, 0), rangeResult.Value[2].Key);
    }

    [Fact]
    public async Task RangeQueryAsync_AcrossMultipleLeafNodes_ReturnsAllEntriesViaBactrakNavigation()
    {
        // Arrange — insert enough keys to force a leaf split (MaxEntries + extras)
        // so the range query must navigate across multiple leaf nodes using
        // the backtrack path through internal nodes.
        var filePath = Path.Combine(_tempDir, "test_range_multileaf.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalKeys = BTreeLeafNode.MaxEntries + 20; // 102 keys → at least 2 leaves
        var insertedKeys = new List<(EmailHashedID Key, long Offset, long BlockId)>();

        for (int i = 1; i <= totalKeys; i++)
        {
            var key = new EmailHashedID((ulong)i, 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            insertedKeys.Add((key, i * 100, i));
        }

        // Confirm multi-level tree was created (leaf split happened)
        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2 for multi-leaf test, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Query a range that spans from the left leaf into the right leaf.
        // With 102 keys, the split puts ~51 in left and ~51 in right.
        // Querying keys 40..65 should cross the leaf boundary.
        var startKey = new EmailHashedID(40, 0, 0, 0);
        var endKey = new EmailHashedID(65, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert — successful result
        Assert.True(rangeResult.IsSuccess, $"Range query failed: {rangeResult.Error}");

        var entries = rangeResult.Value;

        // Assert — correct count: keys 40, 41, 42, ..., 65 = 26 entries
        Assert.Equal(26, entries.Count);

        // Assert — all entries are in sorted order
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i - 1].Key.CompareTo(entries[i].Key) < 0,
                $"Entry at index {i - 1} ({entries[i - 1].Key}) should be less than entry at index {i} ({entries[i].Key})");
        }

        // Assert — first and last entries match expected keys and offsets
        Assert.Equal(new EmailHashedID(40, 0, 0, 0), entries[0].Key);
        Assert.Equal(40 * 100, entries[0].BlockOffset);
        Assert.Equal(new EmailHashedID(65, 0, 0, 0), entries[^1].Key);
        Assert.Equal(65 * 100, entries[^1].BlockOffset);

        // Assert — every key in the range is present with correct data
        for (int i = 0; i < entries.Count; i++)
        {
            ulong expectedKeyVal = (ulong)(40 + i);
            Assert.Equal(new EmailHashedID(expectedKeyVal, 0, 0, 0), entries[i].Key);
            Assert.Equal((long)expectedKeyVal * 100, entries[i].BlockOffset);
            Assert.Equal((long)expectedKeyVal, entries[i].BlockId);
        }
    }

    [Fact]
    public async Task RangeQueryAsync_EntireTreeSpan_ReturnsAllEntriesAcrossAllLeaves()
    {
        // Arrange — insert enough keys for multiple leaves, then query the full range
        var filePath = Path.Combine(_tempDir, "test_range_fullspan.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalKeys = BTreeLeafNode.MaxEntries + 10; // 92 keys
        for (int i = 1; i <= totalKeys; i++)
        {
            var key = new EmailHashedID((ulong)i, 0, 0, 0);
            var r = await btreeIndex.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2);

        // Query the entire key space
        var startKey = new EmailHashedID(1, 0, 0, 0);
        var endKey = new EmailHashedID((ulong)totalKeys, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert — all entries returned
        Assert.True(rangeResult.IsSuccess, $"Range query failed: {rangeResult.Error}");
        Assert.Equal(totalKeys, rangeResult.Value.Count);

        // Assert — sorted order
        for (int i = 1; i < rangeResult.Value.Count; i++)
        {
            Assert.True(rangeResult.Value[i - 1].Key.CompareTo(rangeResult.Value[i].Key) < 0);
        }
    }

    [Fact]
    public async Task RangeQueryAsync_SingleKeyMatchingRange_ReturnsSingleEntry()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test_range_single.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        await btreeIndex.InsertAsync(new EmailHashedID(1, 0, 0, 0), 100, 1);
        await btreeIndex.InsertAsync(new EmailHashedID(5, 0, 0, 0), 500, 5);
        await btreeIndex.InsertAsync(new EmailHashedID(10, 0, 0, 0), 1000, 10);

        // Query range that only matches the middle key
        var startKey = new EmailHashedID(5, 0, 0, 0);
        var endKey = new EmailHashedID(5, 0, 0, 0);

        // Act
        var rangeResult = await btreeIndex.RangeQueryAsync(startKey, endKey);

        // Assert
        Assert.True(rangeResult.IsSuccess);
        Assert.Single(rangeResult.Value);
        Assert.Equal(new EmailHashedID(5, 0, 0, 0), rangeResult.Value[0].Key);
        Assert.Equal(500, rangeResult.Value[0].BlockOffset);
    }
}
