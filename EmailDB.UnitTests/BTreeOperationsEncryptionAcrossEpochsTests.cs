using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-44-5: Verify insert, lookup, delete, and range queries all work correctly
/// when the tree contains nodes encrypted with different key epochs (simulating key
/// rotation during tree growth).
/// </summary>
public class BTreeOperationsEncryptionAcrossEpochsTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeOperationsEncryptionAcrossEpochsTests()
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

    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private static EmailHashedID CreateTestKey(int seed)
    {
        var hash = new byte[32];
        BitConverter.TryWriteBytes(hash.AsSpan(0, 4), seed);
        return new EmailHashedID(hash);
    }

    /// <summary>
    /// Creates a KeyWrappingEncryptionProvider with the given DEKs and active epoch.
    /// </summary>
    private static KeyWrappingEncryptionProvider CreateProvider(
        byte activeEpoch, params (byte epoch, byte[] dek)[] deks)
    {
        var entries = deks.Select(d => new KeyStoreEntry
        {
            Epoch = d.epoch,
            DEK = d.dek,
            Timestamp = DateTime.UtcNow,
            Retired = false
        }).ToList();

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = entries
        };

        return new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);
    }

    [Fact]
    public async Task InsertAndLookup_AcrossTwoEpochs_AllEntriesReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"insert_lookup_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Phase 1: Insert entries with epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        var keysEpoch0 = new List<(EmailHashedID key, long offset, long blockId)>();
        for (int i = 0; i < 10; i++)
        {
            var key = CreateTestKey(i);
            var result = await btree0.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Epoch 0 insert {i} failed: {result.Error}");
            keysEpoch0.Add((key, i * 100, i));
        }

        var rootAfterEpoch0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Phase 2: Rotate to epoch 1, insert more entries
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: rootAfterEpoch0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        var keysEpoch1 = new List<(EmailHashedID key, long offset, long blockId)>();
        for (int i = 10; i < 20; i++)
        {
            var key = CreateTestKey(i);
            var result = await btree1.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Epoch 1 insert {i} failed: {result.Error}");
            keysEpoch1.Add((key, i * 100, i));
        }

        // Verify all epoch 0 entries are still readable
        foreach (var (key, offset, blockId) in keysEpoch0)
        {
            var lookup = await btree1.LookupAsync(key);
            Assert.True(lookup.IsSuccess, $"Epoch 0 key lookup failed: {lookup.Error}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
            Assert.Equal(blockId, lookup.Value.BlockId);
        }

        // Verify all epoch 1 entries are readable
        foreach (var (key, offset, blockId) in keysEpoch1)
        {
            var lookup = await btree1.LookupAsync(key);
            Assert.True(lookup.IsSuccess, $"Epoch 1 key lookup failed: {lookup.Error}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
            Assert.Equal(blockId, lookup.Value.BlockId);
        }

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task InsertAndLookup_AcrossThreeEpochs_AllEntriesReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"three_epochs_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: write 5 entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 5; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: write 5 more entries
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 5; i < 10; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root1 = btree1.CurrentRoot;
        provider1.Dispose();

        // Epoch 2: write 5 more entries, all 3 DEKs available
        using var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);

        for (int i = 10; i < 15; i++)
        {
            var r = await btree2.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // All 15 entries across 3 epochs must be readable
        for (int i = 0; i < 15; i++)
        {
            var lookup = await btree2.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess, $"Key {i} lookup failed: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
            Assert.Equal(i, lookup.Value.BlockId);
        }

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task DeleteAsync_AcrossEpochs_RemovesCorrectEntries()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"delete_epochs_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert 5 entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 5; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert 5 more entries, then delete some from epoch 0
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 5; i < 10; i++)
        {
            var r = await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Delete keys 0, 2, 4 (inserted at epoch 0)
        for (int i = 0; i < 5; i += 2)
        {
            var delResult = await btree1.DeleteAsync(CreateTestKey(i));
            Assert.True(delResult.IsSuccess, $"Delete key {i} failed: {delResult.Error}");
        }

        // Deleted keys should not be found
        for (int i = 0; i < 5; i += 2)
        {
            var lookup = await btree1.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsFailure, $"Key {i} should have been deleted");
        }

        // Remaining epoch 0 keys (1, 3) should still be readable
        for (int i = 1; i < 5; i += 2)
        {
            var lookup = await btree1.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess, $"Key {i} should still exist: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
        }

        // All epoch 1 keys should still be readable
        for (int i = 5; i < 10; i++)
        {
            var lookup = await btree1.LookupAsync(CreateTestKey(i));
            Assert.True(lookup.IsSuccess, $"Key {i} should still exist: {lookup.Error}");
            Assert.Equal(i * 100, lookup.Value.BlockOffset);
        }

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task DeleteAsync_EntryFromEpoch0_WhileActiveEpochIs1_WritesNewBlockAtEpoch1()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"delete_epoch_stamp_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 3; i++)
        {
            var r = await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: delete one entry
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        var delResult = await btree1.DeleteAsync(CreateTestKey(1));
        Assert.True(delResult.IsSuccess);

        // The new leaf block written during delete should be stamped with epoch 1
        var locations = rawBlockManager.GetBlockLocations();
        bool foundEpoch1Leaf = false;
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeLeaf
                && blockResult.Value.IsEncrypted && blockResult.Value.KeyEpoch == 1)
            {
                foundEpoch1Leaf = true;
                break;
            }
        }
        Assert.True(foundEpoch1Leaf, "Delete should have written a new leaf block at epoch 1");

        // Remaining entries still readable
        var lookup0 = await btree1.LookupAsync(CreateTestKey(0));
        Assert.True(lookup0.IsSuccess);
        var lookup2 = await btree1.LookupAsync(CreateTestKey(2));
        Assert.True(lookup2.IsSuccess);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task RangeQuery_AcrossTwoEpochs_ReturnsAllMatchingEntries()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"range_epochs_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        var allKeys = new List<EmailHashedID>();
        for (int i = 0; i < 8; i++)
        {
            var key = CreateTestKey(i);
            allKeys.Add(key);
            var r = await btree0.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more entries
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 8; i < 16; i++)
        {
            var key = CreateTestKey(i);
            allKeys.Add(key);
            var r = await btree1.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess);
        }

        // Sort keys and do a full range query
        allKeys.Sort();
        var rangeResult = await btree1.RangeQueryAsync(allKeys[0], allKeys[^1]);
        Assert.True(rangeResult.IsSuccess, $"RangeQuery failed: {rangeResult.Error}");
        Assert.Equal(16, rangeResult.Value.Count);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task RangeQuery_AcrossThreeEpochs_ReturnsCorrectSubset()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"range_three_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        var allKeys = new List<EmailHashedID>();

        // Epoch 0
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);
        for (int i = 0; i < 5; i++)
        {
            var key = CreateTestKey(i);
            allKeys.Add(key);
            await btree0.InsertAsync(key, i * 100, i);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1
        var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);
        for (int i = 5; i < 10; i++)
        {
            var key = CreateTestKey(i);
            allKeys.Add(key);
            await btree1.InsertAsync(key, i * 100, i);
        }
        var root1 = btree1.CurrentRoot;
        provider1.Dispose();

        // Epoch 2
        using var provider2 = CreateProvider(2, (0, dek0), (1, dek1), (2, dek2));
        var btree2 = new BTreeIndex(rawBlockManager, existingRoot: root1,
            existingRootBlockOffset: -1, encryptionProvider: provider2);
        for (int i = 10; i < 15; i++)
        {
            var key = CreateTestKey(i);
            allKeys.Add(key);
            await btree2.InsertAsync(key, i * 100, i);
        }

        allKeys.Sort();
        var rangeResult = await btree2.RangeQueryAsync(allKeys[0], allKeys[^1]);
        Assert.True(rangeResult.IsSuccess, $"RangeQuery failed: {rangeResult.Error}");
        Assert.Equal(15, rangeResult.Value.Count);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task LargeTree_MultiEpoch_InsertLookupDeleteRangeQuery()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"large_tree_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert enough to create internal nodes
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        int count = BTreeLeafNode.MaxEntries + 10; // Force leaf splits
        var allInserted = new List<(EmailHashedID key, long offset, long blockId)>();

        for (int i = 0; i < count; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree0.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            allInserted.Add((key, i * 100, i));
        }
        Assert.True(btree0.CurrentRoot!.TreeHeight >= 2, "Tree should have internal nodes");
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more entries (tree grows with mixed-epoch nodes)
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = count; i < count + 50; i++)
        {
            var key = CreateTestKey(i);
            var r = await btree1.InsertAsync(key, i * 100, i);
            Assert.True(r.IsSuccess, $"Insert {i} failed: {r.Error}");
            allInserted.Add((key, i * 100, i));
        }

        // Lookup: verify all entries across both epochs
        foreach (var (key, offset, blockId) in allInserted)
        {
            var lookup = await btree1.LookupAsync(key);
            Assert.True(lookup.IsSuccess, $"Lookup failed for blockId {blockId}: {lookup.Error}");
            Assert.Equal(offset, lookup.Value.BlockOffset);
        }

        // Delete: remove some entries from epoch 0
        var deletedKeys = new HashSet<int>();
        for (int i = 0; i < count; i += 10)
        {
            var delResult = await btree1.DeleteAsync(CreateTestKey(i));
            Assert.True(delResult.IsSuccess, $"Delete {i} failed: {delResult.Error}");
            deletedKeys.Add(i);
        }

        // Verify deleted keys are gone, others remain
        for (int i = 0; i < count; i++)
        {
            var lookup = await btree1.LookupAsync(CreateTestKey(i));
            if (deletedKeys.Contains(i))
                Assert.True(lookup.IsFailure, $"Key {i} should have been deleted");
            else
                Assert.True(lookup.IsSuccess, $"Key {i} should still exist: {lookup.Error}");
        }

        // Range query: should return all non-deleted entries
        var sortedKeys = allInserted
            .Where(e => !deletedKeys.Contains((int)e.blockId))
            .Select(e => e.key)
            .ToList();
        sortedKeys.Sort();

        if (sortedKeys.Count > 1)
        {
            var rangeResult = await btree1.RangeQueryAsync(sortedKeys[0], sortedKeys[^1]);
            Assert.True(rangeResult.IsSuccess, $"RangeQuery failed: {rangeResult.Error}");
            Assert.Equal(sortedKeys.Count, rangeResult.Value.Count);
        }

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task OnDiskBlocks_ContainMixedEpochs_AfterRotation()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"mixed_epochs_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entries
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        for (int i = 0; i < 5; i++)
        {
            await btree0.InsertAsync(CreateTestKey(i), i * 100, i);
        }
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: insert more entries
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        for (int i = 5; i < 10; i++)
        {
            await btree1.InsertAsync(CreateTestKey(i), i * 100, i);
        }

        // Verify we have blocks with both epoch 0 and epoch 1 on disk
        var locations = rawBlockManager.GetBlockLocations();
        var epochsSeen = new HashSet<int>();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.IsEncrypted)
            {
                epochsSeen.Add(blockResult.Value.KeyEpoch);
            }
        }

        Assert.Contains(0, epochsSeen);
        Assert.Contains(1, epochsSeen);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task MissingOldEpochDek_FailsOnLookupOfOldEntry()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"missing_dek_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entry
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        var key = CreateTestKey(42);
        await btree0.InsertAsync(key, 4200, 42);
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Reader with only epoch 1 DEK (epoch 0 DEK missing)
        using var providerMissing = CreateProvider(1, (1, dek1));
        var btreeRead = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: providerMissing);

        // Should throw because the leaf was encrypted with epoch 0 DEK which is missing
        await Assert.ThrowsAsync<CryptographicException>(() => btreeRead.LookupAsync(key));

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task InsertAfterDelete_AcrossEpochs_ReinsertedKeyReadable()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"reinsert_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Epoch 0: insert entry
        var provider0 = CreateProvider(0, (0, dek0));
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: provider0);

        var key = CreateTestKey(7);
        await btree0.InsertAsync(key, 700, 7);
        var root0 = btree0.CurrentRoot;
        provider0.Dispose();

        // Epoch 1: delete and re-insert the same key with a new offset
        using var provider1 = CreateProvider(1, (0, dek0), (1, dek1));
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: root0,
            existingRootBlockOffset: -1, encryptionProvider: provider1);

        var delResult = await btree1.DeleteAsync(key);
        Assert.True(delResult.IsSuccess);

        var reinsertResult = await btree1.InsertAsync(key, 7777, 77);
        Assert.True(reinsertResult.IsSuccess);

        // Lookup should return the new offset/blockId
        var lookup = await btree1.LookupAsync(key);
        Assert.True(lookup.IsSuccess, $"Lookup after re-insert failed: {lookup.Error}");
        Assert.Equal(7777, lookup.Value.BlockOffset);
        Assert.Equal(77, lookup.Value.BlockId);

        rawBlockManager.Dispose();
    }
}
