using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeDeleteTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeDeleteTests()
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
    public async Task DeleteAsync_SingleKey_RemovesFromLookup()
    {
        // Arrange - insert a single key, verify it exists
        var filePath = Path.Combine(_tempDir, "test_delete_single.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(10, 20, 30, 40);
        var insertResult = await btreeIndex.InsertAsync(key, 4096, 999);
        Assert.True(insertResult.IsSuccess, $"Insert failed: {insertResult.Error}");

        // Verify lookup succeeds before delete
        var lookupBefore = await btreeIndex.LookupAsync(key);
        Assert.True(lookupBefore.IsSuccess, $"Lookup before delete failed: {lookupBefore.Error}");

        // Act - delete the key
        var deleteResult = await btreeIndex.DeleteAsync(key);

        // Assert - delete succeeded
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert - lookup now fails (key removed)
        var lookupAfter = await btreeIndex.LookupAsync(key);
        Assert.True(lookupAfter.IsFailure, "Lookup should fail after delete");
        Assert.Contains("not found", lookupAfter.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_OneOfMultipleKeys_RemovesOnlyTargetKey()
    {
        // Arrange - insert several keys
        var filePath = Path.Combine(_tempDir, "test_delete_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var keys = new (EmailHashedID Key, long Offset, long BlockId)[]
        {
            (new EmailHashedID(1, 0, 0, 0), 100, 10),
            (new EmailHashedID(5, 0, 0, 0), 500, 50),
            (new EmailHashedID(10, 0, 0, 0), 1000, 100),
            (new EmailHashedID(20, 0, 0, 0), 2000, 200),
            (new EmailHashedID(50, 0, 0, 0), 5000, 500),
        };

        foreach (var (key, offset, blockId) in keys)
        {
            var r = await btreeIndex.InsertAsync(key, offset, blockId);
            Assert.True(r.IsSuccess, $"Insert failed for key {key}: {r.Error}");
        }

        // Act - delete key (10, 0, 0, 0)
        var targetKey = new EmailHashedID(10, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(targetKey);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert - deleted key is not found
        var lookupDeleted = await btreeIndex.LookupAsync(targetKey);
        Assert.True(lookupDeleted.IsFailure, "Deleted key should not be found");

        // Assert - all other keys are still found with correct values
        foreach (var (key, expectedOffset, expectedBlockId) in keys)
        {
            if (key.Equals(targetKey)) continue;

            var lookupResult = await btreeIndex.LookupAsync(key);
            Assert.True(lookupResult.IsSuccess, $"Lookup failed for surviving key {key}: {lookupResult.Error}");
            Assert.Equal(key, lookupResult.Value.Key);
            Assert.Equal(expectedOffset, lookupResult.Value.BlockOffset);
            Assert.Equal(expectedBlockId, lookupResult.Value.BlockId);
        }
    }

    [Fact]
    public async Task DeleteAsync_InMultiLevelTree_RemovesKeyFromLookup()
    {
        // Arrange - build a multi-level tree (height >= 2)
        var filePath = Path.Combine(_tempDir, "test_delete_multilevel.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 10;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Choose a key to delete
        var targetKey = new EmailHashedID(25, 0, 0, 0);
        var lookupBefore = await btreeIndex.LookupAsync(targetKey);
        Assert.True(lookupBefore.IsSuccess, $"Key should exist before delete: {lookupBefore.Error}");

        // Act - delete from multi-level tree
        var deleteResult = await btreeIndex.DeleteAsync(targetKey);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert - deleted key no longer found
        var lookupAfter = await btreeIndex.LookupAsync(targetKey);
        Assert.True(lookupAfter.IsFailure, "Deleted key should not be found in multi-level tree");

        // Assert - other keys still reachable
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            if (k.Equals(targetKey)) continue;

            var lookup = await btreeIndex.LookupAsync(k);
            Assert.True(lookup.IsSuccess, $"Surviving key {i + 1} should still be found: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }
    }

    [Fact]
    public async Task DeleteAsync_MultipleDeletes_RemovesAllTargetedKeys()
    {
        // Arrange - insert 20 keys, delete 5 of them, verify remaining
        var filePath = Path.Combine(_tempDir, "test_delete_multiple.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalKeys = 20;
        for (int i = 0; i < totalKeys; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Act - delete keys 3, 7, 11, 15, 19
        var keysToDelete = new[] { 3, 7, 11, 15, 19 };
        foreach (var keyVal in keysToDelete)
        {
            var k = new EmailHashedID((ulong)keyVal, 0, 0, 0);
            var deleteResult = await btreeIndex.DeleteAsync(k);
            Assert.True(deleteResult.IsSuccess, $"Delete key {keyVal} failed: {deleteResult.Error}");
        }

        // Assert - deleted keys are not found
        var deletedSet = new HashSet<int>(keysToDelete);
        foreach (var keyVal in keysToDelete)
        {
            var k = new EmailHashedID((ulong)keyVal, 0, 0, 0);
            var lookup = await btreeIndex.LookupAsync(k);
            Assert.True(lookup.IsFailure, $"Deleted key {keyVal} should not be found");
        }

        // Assert - surviving keys are still found
        for (int i = 1; i <= totalKeys; i++)
        {
            if (deletedSet.Contains(i)) continue;
            var k = new EmailHashedID((ulong)i, 0, 0, 0);
            var lookup = await btreeIndex.LookupAsync(k);
            Assert.True(lookup.IsSuccess, $"Surviving key {i} should be found: {lookup.Error}");
            Assert.Equal((i - 1) * 100, lookup.Value.BlockOffset);
        }
    }

    [Fact]
    public async Task DeleteAsync_EmptyTree_ReturnsNotFound()
    {
        // Arrange - create an empty tree (no inserts)
        var filePath = Path.Combine(_tempDir, "test_delete_empty.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(99, 99, 99, 99);

        // Act - attempt to delete from empty tree
        var deleteResult = await btreeIndex.DeleteAsync(key);

        // Assert - returns failure with not-found message
        Assert.True(deleteResult.IsFailure, "Delete on empty tree should fail");
        Assert.Contains("not found", deleteResult.Error, StringComparison.OrdinalIgnoreCase);

        // Assert - tree remains empty
        Assert.Null(btreeIndex.CurrentRoot);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_ReturnsNotFoundWithoutModifyingTree()
    {
        // Arrange - insert several keys into the tree
        var filePath = Path.Combine(_tempDir, "test_delete_nonexistent.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var existingKeys = new (EmailHashedID Key, long Offset, long BlockId)[]
        {
            (new EmailHashedID(1, 0, 0, 0), 100, 10),
            (new EmailHashedID(5, 0, 0, 0), 500, 50),
            (new EmailHashedID(10, 0, 0, 0), 1000, 100),
            (new EmailHashedID(20, 0, 0, 0), 2000, 200),
        };

        foreach (var (key, offset, blockId) in existingKeys)
        {
            var r = await btreeIndex.InsertAsync(key, offset, blockId);
            Assert.True(r.IsSuccess, $"Insert failed for key {key}: {r.Error}");
        }

        // Capture tree state before delete attempt
        var rootBefore = btreeIndex.CurrentRoot!;
        var entryCountBefore = rootBefore.EntryCount;
        var treeHeightBefore = rootBefore.TreeHeight;
        var rootOffsetBefore = rootBefore.RootNodeBlockOffset;
        var rootHashBefore = (byte[])rootBefore.RootNodeHash.Clone();

        // Act - attempt to delete a key that was never inserted
        var nonExistentKey = new EmailHashedID(999, 999, 999, 999);
        var deleteResult = await btreeIndex.DeleteAsync(nonExistentKey);

        // Assert - returns failure with not-found message
        Assert.True(deleteResult.IsFailure, "Delete of non-existent key should fail");
        Assert.Contains("not found", deleteResult.Error, StringComparison.OrdinalIgnoreCase);

        // Assert - tree state is completely unchanged
        var rootAfter = btreeIndex.CurrentRoot!;
        Assert.Equal(entryCountBefore, rootAfter.EntryCount);
        Assert.Equal(treeHeightBefore, rootAfter.TreeHeight);
        Assert.Equal(rootOffsetBefore, rootAfter.RootNodeBlockOffset);
        Assert.Equal(rootHashBefore, rootAfter.RootNodeHash);

        // Assert - all original keys are still accessible with correct values
        foreach (var (key, expectedOffset, expectedBlockId) in existingKeys)
        {
            var lookup = await btreeIndex.LookupAsync(key);
            Assert.True(lookup.IsSuccess, $"Key {key} should still be found: {lookup.Error}");
            Assert.Equal(expectedOffset, lookup.Value.BlockOffset);
            Assert.Equal(expectedBlockId, lookup.Value.BlockId);
        }
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKeyInMultiLevelTree_ReturnsNotFoundWithoutModifyingTree()
    {
        // Arrange - build a multi-level tree
        var filePath = Path.Combine(_tempDir, "test_delete_nonexistent_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 10;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Capture tree state before delete attempt
        var rootBefore = btreeIndex.CurrentRoot!;
        var entryCountBefore = rootBefore.EntryCount;
        var treeHeightBefore = rootBefore.TreeHeight;
        var rootOffsetBefore = rootBefore.RootNodeBlockOffset;
        var rootHashBefore = (byte[])rootBefore.RootNodeHash.Clone();

        // Act - attempt to delete a key far outside the inserted range
        var nonExistentKey = new EmailHashedID(99999, 99999, 99999, 99999);
        var deleteResult = await btreeIndex.DeleteAsync(nonExistentKey);

        // Assert - returns failure with not-found message
        Assert.True(deleteResult.IsFailure, "Delete of non-existent key in multi-level tree should fail");
        Assert.Contains("not found", deleteResult.Error, StringComparison.OrdinalIgnoreCase);

        // Assert - tree state is completely unchanged
        var rootAfter = btreeIndex.CurrentRoot!;
        Assert.Equal(entryCountBefore, rootAfter.EntryCount);
        Assert.Equal(treeHeightBefore, rootAfter.TreeHeight);
        Assert.Equal(rootOffsetBefore, rootAfter.RootNodeBlockOffset);
        Assert.Equal(rootHashBefore, rootAfter.RootNodeHash);
    }

    [Fact]
    public async Task DeleteAsync_UnderflowTriggersMergeWithSiblingViaParent()
    {
        // Arrange — build a tree with 3 leaves (height 2, internal root with 2 keys)
        // Insert exactly MaxEntries + MaxEntries/2 + 1 sequential keys to force two splits:
        //   Split 1 at insert 83: left(41 entries), right(42 entries)
        //   Split 2 at insert 124: left(41), middle(41), right(42)
        var filePath = Path.Combine(_tempDir, "test_merge_underflow.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + BTreeLeafNode.MaxEntries / 2 + 1; // 124

        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");
        Assert.Equal(totalInserts, btreeIndex.CurrentRoot.EntryCount);

        // Act — delete key 1 from the leftmost leaf to trigger underflow
        // Left leaf has exactly MinEntries (41) entries after splits;
        // removing one drops it to 40 < MinEntries, triggering merge with its sibling
        var keyToDelete = new EmailHashedID(1, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(keyToDelete);

        // Assert — delete succeeded
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — entry count decremented
        Assert.Equal(totalInserts - 1, btreeIndex.CurrentRoot!.EntryCount);

        // Assert — tree height maintained (merge of 3→2 leaves, no root collapse)
        Assert.Equal(2, btreeIndex.CurrentRoot.TreeHeight);

        // Assert — deleted key is not found
        var lookupDeleted = await btreeIndex.LookupAsync(keyToDelete);
        Assert.True(lookupDeleted.IsFailure, "Deleted key should not be found");

        // Assert — all surviving keys are still accessible with correct values
        for (int i = 2; i <= totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)i, 0, 0, 0);
            var lookup = await btreeIndex.LookupAsync(k);
            Assert.True(lookup.IsSuccess, $"Surviving key {i} should still be found: {lookup.Error}");
            Assert.Equal((i - 1) * 100, lookup.Value.BlockOffset);
            Assert.Equal(i - 1, lookup.Value.BlockId);
        }
    }

    [Fact]
    public async Task DeleteAsync_EntryCountDecrementedCorrectly()
    {
        // Arrange - insert keys into a single-level tree
        var filePath = Path.Combine(_tempDir, "test_delete_entrycount.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalKeys = 10;
        for (int i = 0; i < totalKeys; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.Equal(totalKeys, btreeIndex.CurrentRoot!.EntryCount);

        // Act & Assert - delete keys one at a time, verify EntryCount after each
        for (int i = 0; i < 5; i++)
        {
            var keyToDelete = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var deleteResult = await btreeIndex.DeleteAsync(keyToDelete);
            Assert.True(deleteResult.IsSuccess, $"Delete key {i + 1} failed: {deleteResult.Error}");
            Assert.Equal(totalKeys - (i + 1), btreeIndex.CurrentRoot!.EntryCount);
        }

        // Verify final state: 5 keys remain
        Assert.Equal(5, btreeIndex.CurrentRoot!.EntryCount);
    }

    [Fact]
    public async Task DeleteAsync_EntryCountDecrementedCorrectly_MultiLevelTree()
    {
        // Arrange - build a multi-level tree
        var filePath = Path.Combine(_tempDir, "test_delete_entrycount_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 10;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");
        Assert.Equal(totalInserts, btreeIndex.CurrentRoot.EntryCount);

        // Act & Assert - delete 3 keys from different positions, check count each time
        var keysToDelete = new[]
        {
            new EmailHashedID(1, 0, 0, 0),                          // first key
            new EmailHashedID((ulong)(totalInserts / 2), 0, 0, 0),  // middle key
            new EmailHashedID((ulong)totalInserts, 0, 0, 0),        // last key
        };

        for (int i = 0; i < keysToDelete.Length; i++)
        {
            var deleteResult = await btreeIndex.DeleteAsync(keysToDelete[i]);
            Assert.True(deleteResult.IsSuccess, $"Delete {i} failed: {deleteResult.Error}");
            Assert.Equal(totalInserts - (i + 1), btreeIndex.CurrentRoot!.EntryCount);
        }
    }

    [Fact]
    public async Task DeleteAsync_OldLeafBlocksRemainInFile_AppendOnly()
    {
        // Arrange - insert several keys into a single-level tree
        var filePath = Path.Combine(_tempDir, "test_delete_appendonly.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalKeys = 10;
        for (int i = 0; i < totalKeys; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Capture state before delete: all block IDs and file size
        var blockLocationsBefore = rawBlockManager.GetBlockLocations()
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        long fileSizeBefore = new FileInfo(filePath).Length;
        int blockCountBefore = blockLocationsBefore.Count;

        Assert.True(blockCountBefore > 0, "Should have blocks after inserts");
        Assert.True(fileSizeBefore > 0, "File should have content after inserts");

        // Act - delete a key (triggers copy-on-write: new leaf + new IndexRoot appended)
        var keyToDelete = new EmailHashedID(5, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(keyToDelete);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert - file size increased (new blocks appended, old blocks not removed)
        long fileSizeAfter = new FileInfo(filePath).Length;
        Assert.True(fileSizeAfter > fileSizeBefore,
            $"File should grow after delete (append-only). Before: {fileSizeBefore}, After: {fileSizeAfter}");

        // Assert - all original block IDs are still tracked in block locations
        var blockLocationsAfter = rawBlockManager.GetBlockLocations();
        foreach (var oldBlockId in blockLocationsBefore.Keys)
        {
            Assert.True(blockLocationsAfter.ContainsKey(oldBlockId),
                $"Old block ID {oldBlockId} should still be in block locations (append-only)");
        }

        // Assert - more blocks exist now (new leaf + new IndexRoot were appended)
        Assert.True(blockLocationsAfter.Count > blockCountBefore,
            $"Block count should increase after delete. Before: {blockCountBefore}, After: {blockLocationsAfter.Count}");

        // Assert - old blocks are still readable with valid data
        foreach (var (oldBlockId, oldLocation) in blockLocationsBefore)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(oldBlockId);
            Assert.True(readResult.IsSuccess,
                $"Old block {oldBlockId} at position {oldLocation.Position} should still be readable: {readResult.Error}");
            Assert.Equal(oldBlockId, readResult.Value.BlockId);
        }

        // Assert - old block positions haven't moved
        foreach (var (oldBlockId, oldLocation) in blockLocationsBefore)
        {
            var currentLocation = blockLocationsAfter[oldBlockId];
            Assert.Equal(oldLocation.Position, currentLocation.Position);
            Assert.Equal(oldLocation.Length, currentLocation.Length);
        }
    }

    [Fact]
    public async Task DeleteAsync_OldLeafBlocksRemainInFile_MultiLevelTree()
    {
        // Arrange - build a multi-level tree
        var filePath = Path.Combine(_tempDir, "test_delete_appendonly_multi.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 10;
        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        Assert.True(btreeIndex.CurrentRoot!.TreeHeight >= 2,
            $"Expected height >= 2, got {btreeIndex.CurrentRoot.TreeHeight}");

        // Capture state before delete
        var blockLocationsBefore = rawBlockManager.GetBlockLocations()
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        long fileSizeBefore = new FileInfo(filePath).Length;
        int blockCountBefore = blockLocationsBefore.Count;

        // Act - delete a key from the multi-level tree
        var keyToDelete = new EmailHashedID(25, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(keyToDelete);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert - file grew (append-only)
        long fileSizeAfter = new FileInfo(filePath).Length;
        Assert.True(fileSizeAfter > fileSizeBefore,
            $"File should grow after multi-level delete. Before: {fileSizeBefore}, After: {fileSizeAfter}");

        // Assert - all original block IDs still present
        var blockLocationsAfter = rawBlockManager.GetBlockLocations();
        foreach (var oldBlockId in blockLocationsBefore.Keys)
        {
            Assert.True(blockLocationsAfter.ContainsKey(oldBlockId),
                $"Old block ID {oldBlockId} should still be tracked after multi-level delete");
        }

        // Assert - block count increased (new leaf + internal nodes + IndexRoot)
        Assert.True(blockLocationsAfter.Count > blockCountBefore,
            $"Block count should increase. Before: {blockCountBefore}, After: {blockLocationsAfter.Count}");

        // Assert - old blocks are still readable at their original positions
        foreach (var (oldBlockId, oldLocation) in blockLocationsBefore)
        {
            var readResult = await rawBlockManager.ReadBlockAsync(oldBlockId);
            Assert.True(readResult.IsSuccess,
                $"Old block {oldBlockId} at position {oldLocation.Position} should still be readable: {readResult.Error}");

            // Verify position hasn't changed
            var currentLocation = blockLocationsAfter[oldBlockId];
            Assert.Equal(oldLocation.Position, currentLocation.Position);
        }
    }

    [Fact]
    public async Task DeleteAsync_ThenReinsert_KeyIsFoundAgain()
    {
        // Arrange - insert, delete, then re-insert the same key
        var filePath = Path.Combine(_tempDir, "test_delete_reinsert.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = new EmailHashedID(42, 84, 126, 168);
        await btreeIndex.InsertAsync(key, 1000, 100);

        // Act - delete
        var deleteResult = await btreeIndex.DeleteAsync(key);
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Verify gone
        var lookupGone = await btreeIndex.LookupAsync(key);
        Assert.True(lookupGone.IsFailure, "Key should be gone after delete");

        // Re-insert with different values
        var reinsertResult = await btreeIndex.InsertAsync(key, 2000, 200);
        Assert.True(reinsertResult.IsSuccess, $"Re-insert failed: {reinsertResult.Error}");

        // Assert - key found with new values
        var lookupAfter = await btreeIndex.LookupAsync(key);
        Assert.True(lookupAfter.IsSuccess, $"Lookup after re-insert failed: {lookupAfter.Error}");
        Assert.Equal(2000, lookupAfter.Value.BlockOffset);
        Assert.Equal(200, lookupAfter.Value.BlockId);
    }

    [Fact]
    public async Task DeleteAsync_RootCollapse_ProducesCorrectSingleLevelTree()
    {
        // Arrange — build a height-2 tree with exactly 2 leaves by inserting MaxEntries + 1 keys.
        // Split produces: left leaf (41 entries), right leaf (42 entries), internal root (1 key).
        var filePath = Path.Combine(_tempDir, "test_root_collapse.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        int totalInserts = BTreeLeafNode.MaxEntries + 1; // 83

        for (int i = 0; i < totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)(i + 1), 0, 0, 0);
            var r = await btreeIndex.InsertAsync(k, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
        }

        // Verify we have a height-2 tree
        Assert.Equal(2, btreeIndex.CurrentRoot!.TreeHeight);
        Assert.Equal(totalInserts, btreeIndex.CurrentRoot.EntryCount);

        // Act — delete a key from the left leaf to trigger underflow.
        // Left leaf has exactly MinEntries (41) entries; removing one drops to 40 < MinEntries.
        // Combined 40 + 42 = 82 = MaxEntries, so merge succeeds.
        // Parent had 1 key; after merge it has 0 keys → root collapse → height drops to 1.
        var keyToDelete = new EmailHashedID(1, 0, 0, 0);
        var deleteResult = await btreeIndex.DeleteAsync(keyToDelete);

        // Assert — delete succeeded
        Assert.True(deleteResult.IsSuccess, $"Delete failed: {deleteResult.Error}");

        // Assert — tree collapsed to a single-level tree (height 1)
        Assert.Equal(1, btreeIndex.CurrentRoot!.TreeHeight);

        // Assert — entry count is correct
        Assert.Equal(totalInserts - 1, btreeIndex.CurrentRoot.EntryCount);

        // Assert — deleted key is not found
        var lookupDeleted = await btreeIndex.LookupAsync(keyToDelete);
        Assert.True(lookupDeleted.IsFailure, "Deleted key should not be found after root collapse");

        // Assert — all surviving keys are still accessible with correct values
        for (int i = 2; i <= totalInserts; i++)
        {
            var k = new EmailHashedID((ulong)i, 0, 0, 0);
            var lookup = await btreeIndex.LookupAsync(k);
            Assert.True(lookup.IsSuccess, $"Surviving key {i} should be found after root collapse: {lookup.Error}");
            Assert.Equal((i - 1) * 100, lookup.Value.BlockOffset);
            Assert.Equal(i - 1, lookup.Value.BlockId);
        }

        // Assert — tree is functional: can still insert new keys into the collapsed tree
        var newKey = new EmailHashedID(999, 0, 0, 0);
        var insertAfterCollapse = await btreeIndex.InsertAsync(newKey, 9999, 888);
        Assert.True(insertAfterCollapse.IsSuccess, $"Insert after collapse failed: {insertAfterCollapse.Error}");

        var lookupNew = await btreeIndex.LookupAsync(newKey);
        Assert.True(lookupNew.IsSuccess, $"Lookup of new key after collapse failed: {lookupNew.Error}");
        Assert.Equal(9999, lookupNew.Value.BlockOffset);
        Assert.Equal(888, lookupNew.Value.BlockId);
    }
}
