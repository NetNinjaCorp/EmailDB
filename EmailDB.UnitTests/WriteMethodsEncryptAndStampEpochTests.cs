using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// US-EMDB-44-2: Verify B-tree write methods encrypt node payloads using the active DEK
/// from KeyWrappingEncryptionProvider and stamp the active key epoch into bits 1-7 of
/// the Flags byte.
/// </summary>
public class WriteMethodsEncryptAndStampEpochTests : IDisposable
{
    private readonly string _tempDir;

    public WriteMethodsEncryptAndStampEpochTests()
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

    private (BTreeIndex btree, RawBlockManager rawBlockManager, KeyWrappingEncryptionProvider provider, byte[] dek)
        CreateEncryptedBTreeIndex(byte activeEpoch = 0, byte[]? dek = null)
    {
        var filePath = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.emdb");
        var rawBlockManager = new RawBlockManager(filePath);

        dek ??= GenerateDek();

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = activeEpoch,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = activeEpoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        // Use Full policy so BTreeLeaf, BTreeInternal, and IndexRoot are encrypted
        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);
        var btreeIndex = new BTreeIndex(rawBlockManager, encryptionProvider: provider);

        return (btreeIndex, rawBlockManager, provider, dek);
    }

    [Fact]
    public async Task InsertAsync_WithEncryptionProvider_LeafBlockHasEncryptedFlag()
    {
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex();
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        var result = await btree.InsertAsync(key, 100, 1);
        Assert.True(result.IsSuccess);

        // Read back raw blocks and find the BTreeLeaf block
        var locations = rawBlockManager.GetBlockLocations();
        Block? leafBlock = null;
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeLeaf)
            {
                leafBlock = blockResult.Value;
                break;
            }
        }

        Assert.NotNull(leafBlock);
        Assert.True(leafBlock!.IsEncrypted, "Bit 0 (encrypted flag) should be set on BTreeLeaf block");
    }

    [Fact]
    public async Task InsertAsync_WithEncryptionProvider_LeafBlockHasCorrectKeyEpoch()
    {
        byte activeEpoch = 7;
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(activeEpoch: activeEpoch);
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        await btree.InsertAsync(key, 100, 1);

        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeLeaf)
            {
                Assert.Equal(activeEpoch, blockResult.Value.KeyEpoch);
                byte expectedFlags = (byte)((activeEpoch << 1) | Block.FlagEncrypted);
                Assert.Equal(expectedFlags, blockResult.Value.Flags);
                return;
            }
        }
        Assert.Fail("No BTreeLeaf block found");
    }

    [Fact]
    public async Task InsertAsync_WithEncryptionProvider_PayloadIsEncrypted()
    {
        var dek = GenerateDek();
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(dek: dek);
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        await btree.InsertAsync(key, 100, 1);

        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeLeaf)
            {
                var block = blockResult.Value;
                // Encrypted payload should include AES-GCM overhead (12 nonce + 16 tag = 28 bytes)
                Assert.True(block.Payload.Length >= 28,
                    "Encrypted payload should include AES-GCM overhead");

                // Verify the payload is decryptable with the correct DEK
                using var standalone = new AesGcmBlockEncryptionProvider(dek);
                var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);
                Assert.NotEmpty(decrypted);
                return;
            }
        }
        Assert.Fail("No BTreeLeaf block found");
    }

    [Fact]
    public async Task InsertAsync_WithEncryptionProvider_IndexRootBlockIsEncrypted()
    {
        var dek = GenerateDek();
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(dek: dek);
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        await btree.InsertAsync(key, 100, 1);

        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.IndexRoot)
            {
                var block = blockResult.Value;
                Assert.True(block.IsEncrypted, "IndexRoot block should be encrypted under Full policy");
                Assert.Equal(0, block.KeyEpoch); // epoch 0 is default

                // Verify decryptable
                using var standalone = new AesGcmBlockEncryptionProvider(dek);
                var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);
                Assert.NotEmpty(decrypted);
                return;
            }
        }
        Assert.Fail("No IndexRoot block found");
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)42)]
    [InlineData((byte)127)]
    public async Task InsertAsync_VariousEpochs_StampsCorrectEpochInFlags(byte epoch)
    {
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(activeEpoch: epoch);
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        await btree.InsertAsync(key, 100, 1);

        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.BTreeLeaf)
            {
                Assert.True(blockResult.Value.IsEncrypted);
                Assert.Equal(epoch, blockResult.Value.KeyEpoch);
                byte expectedFlags = (byte)((epoch << 1) | Block.FlagEncrypted);
                Assert.Equal(expectedFlags, blockResult.Value.Flags);
                return;
            }
        }
        Assert.Fail("No BTreeLeaf block found");
    }

    [Fact]
    public async Task InsertAsync_WithoutEncryptionProvider_BlocksNotEncrypted()
    {
        var filePath = Path.Combine(_tempDir, "no_encryption.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        var key = CreateTestKey(1);
        await btreeIndex.InsertAsync(key, 100, 1);

        var locations = rawBlockManager.GetBlockLocations();
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess)
            {
                Assert.False(blockResult.Value.IsEncrypted,
                    $"Block type {blockResult.Value.Type} should NOT be encrypted without provider");
                Assert.Equal(0, blockResult.Value.KeyEpoch);
            }
        }
    }

    [Fact]
    public async Task InsertAsync_EncryptedPayload_DecryptableWithActiveDek()
    {
        var dek = GenerateDek();
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(dek: dek);
        using var _ = rawBlockManager;
        using var __ = provider;

        var key = CreateTestKey(1);
        await btree.InsertAsync(key, 100, 1);

        // Read all blocks and verify every encrypted block is decryptable
        var locations = rawBlockManager.GetBlockLocations();
        using var standalone = new AesGcmBlockEncryptionProvider(dek);

        int encryptedCount = 0;
        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess && blockResult.Value.IsEncrypted)
            {
                var block = blockResult.Value;
                var decrypted = standalone.Decrypt(block.Payload, block.Type, block.BlockId);
                Assert.NotNull(decrypted);
                Assert.NotEmpty(decrypted);
                encryptedCount++;
            }
        }

        Assert.True(encryptedCount >= 2, "Expected at least 2 encrypted blocks (leaf + IndexRoot)");
    }

    [Fact]
    public async Task DeleteAsync_WithEncryptionProvider_WrittenBlocksAreEncrypted()
    {
        var dek = GenerateDek();
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(dek: dek);
        using var _ = rawBlockManager;
        using var __ = provider;

        // Insert two keys so delete has something to work with
        var key1 = CreateTestKey(1);
        var key2 = CreateTestKey(2);
        await btree.InsertAsync(key1, 100, 1);
        await btree.InsertAsync(key2, 200, 2);

        // Track block count before delete
        var locationsBefore = rawBlockManager.GetBlockLocations().Count;

        // Delete one key
        var deleteResult = await btree.DeleteAsync(key1);
        Assert.True(deleteResult.IsSuccess);

        // New blocks written by delete should also be encrypted
        var locationsAfter = rawBlockManager.GetBlockLocations();
        using var standalone = new AesGcmBlockEncryptionProvider(dek);

        foreach (var kvp in locationsAfter)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (blockResult.IsSuccess)
            {
                var block = blockResult.Value;
                if (block.Type == BlockType.BTreeLeaf || block.Type == BlockType.BTreeInternal || block.Type == BlockType.IndexRoot)
                {
                    Assert.True(block.IsEncrypted,
                        $"Block type {block.Type} (ID {block.BlockId}) should be encrypted");
                    Assert.Equal(0, block.KeyEpoch);
                }
            }
        }
    }

    [Fact]
    public async Task InsertAsync_AllBTreeBlockTypes_AreEncryptedUnderFullPolicy()
    {
        var dek = GenerateDek();
        var (btree, rawBlockManager, provider, _) = CreateEncryptedBTreeIndex(dek: dek);
        using var _ = rawBlockManager;
        using var __ = provider;

        // Insert enough keys to trigger a leaf split (creates internal node)
        // BTreeLeafNode.MaxEntries is typically small enough
        for (int i = 0; i < 200; i++)
        {
            var key = CreateTestKey(i);
            var result = await btree.InsertAsync(key, i * 100, i);
            Assert.True(result.IsSuccess, $"Insert {i} failed: {result.Error}");
        }

        // Verify all BTree block types are encrypted
        var locations = rawBlockManager.GetBlockLocations();
        using var standalone = new AesGcmBlockEncryptionProvider(dek);

        bool foundLeaf = false, foundInternal = false, foundRoot = false;

        foreach (var kvp in locations)
        {
            var blockResult = await rawBlockManager.ReadBlockAsync(kvp.Key);
            if (!blockResult.IsSuccess) continue;

            var block = blockResult.Value;
            switch (block.Type)
            {
                case BlockType.BTreeLeaf:
                    foundLeaf = true;
                    Assert.True(block.IsEncrypted, "BTreeLeaf should be encrypted");
                    standalone.Decrypt(block.Payload, block.Type, block.BlockId); // should not throw
                    break;
                case BlockType.BTreeInternal:
                    foundInternal = true;
                    Assert.True(block.IsEncrypted, "BTreeInternal should be encrypted");
                    standalone.Decrypt(block.Payload, block.Type, block.BlockId);
                    break;
                case BlockType.IndexRoot:
                    foundRoot = true;
                    Assert.True(block.IsEncrypted, "IndexRoot should be encrypted");
                    standalone.Decrypt(block.Payload, block.Type, block.BlockId);
                    break;
            }
        }

        Assert.True(foundLeaf, "Should have at least one BTreeLeaf block");
        Assert.True(foundInternal, "Should have at least one BTreeInternal block after enough inserts");
        Assert.True(foundRoot, "Should have at least one IndexRoot block");
    }
}
