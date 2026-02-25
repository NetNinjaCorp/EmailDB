using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-44-3: Verify B-tree read methods (LookupAsync, RangeQueryAsync) extract
/// the key epoch from Flags bits 1-7, pass it to KeyWrappingEncryptionProvider to
/// look up the correct DEK, and decrypt before deserializing the node.
/// </summary>
public class ReadMethodsDecryptBeforeDeserializationTests : IDisposable
{
    private readonly string _tempDir;

    public ReadMethodsDecryptBeforeDeserializationTests()
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

    private (BTreeIndex btree, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider)
        CreateEncryptedBTreeIndex(byte activeEpoch = 0, List<KeyStoreEntry>? entries = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        KeyStoreContent keyStore;
        if (entries != null)
        {
            keyStore = new KeyStoreContent
            {
                ActiveEpoch = activeEpoch,
                Entries = entries
            };
        }
        else
        {
            keyStore = new KeyStoreContent
            {
                ActiveEpoch = activeEpoch,
                Entries = new List<KeyStoreEntry>
                {
                    new() { Epoch = activeEpoch, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
                }
            };
        }

        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);
        var btreeIndex = new BTreeIndex(rawBlockManager, encryptionProvider: provider);

        return (btreeIndex, rawBlockManager, provider);
    }

    [Fact]
    public async Task LookupAsync_DecryptsLeafNodeUsingKeyEpochFromFlags()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        var insertResult = await btree.InsertAsync(key, 100, 1);
        Assert.True(insertResult.IsSuccess);

        // Verify the on-disk leaf block is actually encrypted
        var locations = rawBlockManager.GetBlockLocations();
        bool foundEncrypted = false;
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeLeaf)
            {
                Assert.True(blockResult.Value.IsEncrypted, "Leaf block should be encrypted on disk");
                foundEncrypted = true;
            }
        }
        Assert.True(foundEncrypted, "Should have found an encrypted BTreeLeaf block");

        // LookupAsync must decrypt the leaf before deserialization to find the entry
        var lookupResult = await btree.LookupAsync(key);
        Assert.True(lookupResult.IsSuccess, $"LookupAsync failed: {lookupResult.Error}");
        Assert.Equal(100, lookupResult.Value.BlockOffset);
        Assert.Equal(1, lookupResult.Value.BlockId);
    }

    [Fact]
    public async Task LookupAsync_MultiEpoch_UsesEpochFromFlagsNotActiveEpoch()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        // Write entries with epoch 0
        var filePath = Path.Combine(_tempDir, $"multi_epoch_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        var keyStoreEpoch0 = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var providerEpoch0 = new KeyWrappingEncryptionProvider(keyStoreEpoch0, EncryptionPolicy.Full);
        var btreeWrite = new BTreeIndex(rawBlockManager, encryptionProvider: providerEpoch0);

        var key = CreateTestKey(42);
        var insertResult = await btreeWrite.InsertAsync(key, 500, 10);
        Assert.True(insertResult.IsSuccess);

        // Verify blocks were written with epoch 0
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.IsEncrypted)
                Assert.Equal(0, blockResult.Value.KeyEpoch);
        }

        providerEpoch0.Dispose();

        // Create a reader with both epochs loaded but epoch 1 is active
        var keyStoreBoth = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerBoth = new KeyWrappingEncryptionProvider(keyStoreBoth, EncryptionPolicy.Full);

        // Reconstruct BTreeIndex with the existing root so LookupAsync can navigate the tree
        var btreeRead = new BTreeIndex(rawBlockManager, existingRoot: btreeWrite.CurrentRoot,
            existingRootBlockOffset: -1, encryptionProvider: providerBoth);

        // LookupAsync should read epoch 0 from Flags and use dek0 (not the active dek1)
        var lookupResult = await btreeRead.LookupAsync(key);
        Assert.True(lookupResult.IsSuccess, $"LookupAsync failed: {lookupResult.Error}");
        Assert.Equal(500, lookupResult.Value.BlockOffset);
        Assert.Equal(10, lookupResult.Value.BlockId);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task LookupAsync_MissingEpochDek_ThrowsCryptographicException()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"wrong_epoch_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Write with epoch 0
        var keyStoreWrite = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerWrite = new KeyWrappingEncryptionProvider(keyStoreWrite, EncryptionPolicy.Full);
        var btreeWrite = new BTreeIndex(rawBlockManager, encryptionProvider: providerWrite);

        var key = CreateTestKey(1);
        await btreeWrite.InsertAsync(key, 100, 1);

        // Read with only epoch 1 (no epoch 0 DEK available)
        var keyStoreRead = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerRead = new KeyWrappingEncryptionProvider(keyStoreRead, EncryptionPolicy.Full);
        var btreeRead = new BTreeIndex(rawBlockManager, existingRoot: btreeWrite.CurrentRoot,
            existingRootBlockOffset: -1, encryptionProvider: providerRead);

        // Should throw because epoch 0 DEK is not available in the reader's key store
        await Assert.ThrowsAsync<CryptographicException>(() => btreeRead.LookupAsync(key));

        rawBlockManager.Dispose();
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)42)]
    [InlineData((byte)127)]
    public async Task LookupAsync_VariousEpochs_DecryptsCorrectly(byte epoch)
    {
        var dek = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex(activeEpoch: epoch, entries: entries);
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(99);
        var insertResult = await btree.InsertAsync(key, 777, 3);
        Assert.True(insertResult.IsSuccess);

        // Verify epoch stamped correctly on disk
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.IsEncrypted)
                Assert.Equal(epoch, blockResult.Value.KeyEpoch);
        }

        // LookupAsync reads epoch from Flags and decrypts with the correct DEK
        var lookupResult = await btree.LookupAsync(key);
        Assert.True(lookupResult.IsSuccess, $"LookupAsync failed for epoch {epoch}: {lookupResult.Error}");
        Assert.Equal(777, lookupResult.Value.BlockOffset);
    }

    [Fact]
    public async Task RangeQueryAsync_DecryptsNodesBeforeDeserialization()
    {
        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        // Insert several entries
        var keys = new List<EmailHashedID>();
        for (int i = 0; i < 10; i++)
        {
            var key = CreateTestKey(i);
            keys.Add(key);
            var result = await btree.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess);
        }

        // Sort keys to know the range bounds
        keys.Sort();

        // RangeQueryAsync must decrypt all traversed nodes to return results
        var rangeResult = await btree.RangeQueryAsync(keys[0], keys[^1]);
        Assert.True(rangeResult.IsSuccess, $"RangeQueryAsync failed: {rangeResult.Error}");
        Assert.Equal(10, rangeResult.Value.Count);
    }

    [Fact]
    public async Task RangeQueryAsync_MultiEpoch_DecryptsAllNodesCorrectly()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        var filePath = Path.Combine(_tempDir, $"range_multi_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        // Write initial entries with epoch 0
        var keyStoreEpoch0 = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var providerEpoch0 = new KeyWrappingEncryptionProvider(keyStoreEpoch0, EncryptionPolicy.Full);
        var btree0 = new BTreeIndex(rawBlockManager, encryptionProvider: providerEpoch0);

        var key1 = CreateTestKey(10);
        var key2 = CreateTestKey(20);
        await btree0.InsertAsync(key1, 100, 1);
        await btree0.InsertAsync(key2, 200, 2);

        var rootAfterEpoch0 = btree0.CurrentRoot;
        providerEpoch0.Dispose();

        // Now write more entries with epoch 1 (both DEKs available)
        var keyStoreBoth = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerBoth = new KeyWrappingEncryptionProvider(keyStoreBoth, EncryptionPolicy.Full);
        var btree1 = new BTreeIndex(rawBlockManager, existingRoot: rootAfterEpoch0,
            existingRootBlockOffset: -1, encryptionProvider: providerBoth);

        var key3 = CreateTestKey(30);
        await btree1.InsertAsync(key3, 300, 3);

        // Sort all keys
        var allKeys = new List<EmailHashedID> { key1, key2, key3 };
        allKeys.Sort();

        // RangeQueryAsync must handle blocks encrypted with different epochs
        var rangeResult = await btree1.RangeQueryAsync(allKeys[0], allKeys[^1]);
        Assert.True(rangeResult.IsSuccess, $"RangeQueryAsync failed: {rangeResult.Error}");
        Assert.Equal(3, rangeResult.Value.Count);

        rawBlockManager.Dispose();
    }

    [Fact]
    public async Task LookupAsync_WithoutEncryptionProvider_ReadsNormally()
    {
        var filePath = Path.Combine(_tempDir, "no_enc.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btree = new BTreeIndex(rawBlockManager);

        var key = CreateTestKey(1);
        var insertResult = await btree.InsertAsync(key, 100, 1);
        Assert.True(insertResult.IsSuccess);

        // Verify blocks are not encrypted
        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess)
            {
                Assert.False(blockResult.Value.IsEncrypted);
                Assert.Equal(0, blockResult.Value.KeyEpoch);
            }
        }

        // LookupAsync works without decryption
        var lookupResult = await btree.LookupAsync(key);
        Assert.True(lookupResult.IsSuccess);
        Assert.Equal(100, lookupResult.Value.BlockOffset);
    }

    [Fact]
    public async Task LookupAsync_InternalAndLeafNodes_BothDecryptedBeforeDeserialization()
    {
        var dek = GenerateDek();
        var entries = new List<KeyStoreEntry>
        {
            new() { Epoch = 0, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
        };

        var (btree, rawBlockManager, provider) = CreateEncryptedBTreeIndex(entries: entries);
        using var _ = rawBlockManager;
        using var __ = provider;

        // Insert enough keys to trigger leaf splits and create internal nodes
        var insertedKeys = new List<(EmailHashedID key, long offset, long blockId)>();
        for (int i = 0; i < 200; i++)
        {
            var key = CreateTestKey(i);
            var result = await btree.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");
            insertedKeys.Add((key, i * 100, i));
        }

        // Verify internal nodes exist and are encrypted on disk
        var locations = rawBlockManager.GetBlockLocations();
        bool foundInternal = false;
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeInternal)
            {
                Assert.True(blockResult.Value.IsEncrypted, "Internal node should be encrypted");
                Assert.Equal(0, blockResult.Value.KeyEpoch);
                foundInternal = true;
            }
        }
        Assert.True(foundInternal, "Should have BTreeInternal blocks after 200 inserts");

        // LookupAsync must decrypt both internal and leaf nodes to find an entry
        // Pick a key from the middle of the range
        var (lookupKey, expectedOffset, expectedBlockId) = insertedKeys[100];
        var lookupResult = await btree.LookupAsync(lookupKey);
        Assert.True(lookupResult.IsSuccess, $"LookupAsync failed: {lookupResult.Error}");
        Assert.Equal(expectedOffset, lookupResult.Value.BlockOffset);
        Assert.Equal(expectedBlockId, lookupResult.Value.BlockId);
    }
}
