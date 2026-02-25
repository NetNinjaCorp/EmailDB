using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that on file creation, an initial DEK epoch 0 is generated
/// and a key store block is written to the database file.
/// </summary>
public class InitialDekEpoch0KeyStoreBlockTests : IDisposable
{
    private const int KeySize = 32;
    private readonly string _tempFile;

    public InitialDekEpoch0KeyStoreBlockTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}.emdb");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    private static byte[] GenerateKek()
    {
        var kek = new byte[KeySize];
        RandomNumberGenerator.Fill(kek);
        return kek;
    }

    /// <summary>
    /// Simulates the file-creation path: generate epoch-0 DEK, encrypt key store,
    /// write as a KeyStore block, then verify it persists on disk.
    /// </summary>
    private static (KeyStoreContent content, byte[] kek, byte[] encryptedPayload) CreateInitialKeyStore()
    {
        var kek = GenerateKek();
        var dek = new byte[KeySize];
        RandomNumberGenerator.Fill(dek);

        var content = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new()
                {
                    Epoch = 0,
                    DEK = dek,
                    Timestamp = DateTime.UtcNow,
                    Retired = false
                }
            }
        };

        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var encrypted = ksManager.EncryptKeyStore(content, kek);

        return (content, kek, encrypted);
    }

    [Fact]
    public async Task FileCreation_WritesKeyStoreBlock_WithEpoch0Dek()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        var writeResult = await rbm.WriteBlockAsync(block);

        Assert.True(writeResult.IsSuccess, $"WriteBlockAsync failed: {writeResult.Error}");
    }

    [Fact]
    public async Task FileCreation_KeyStoreBlock_RoundTripsCorrectly()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        Assert.True(readResult.IsSuccess, $"ReadBlockAsync failed: {readResult.Error}");

        var readBlock = readResult.Value;
        Assert.Equal(BlockType.KeyStore, readBlock.Type);
        Assert.True(readBlock.IsEncrypted, "KeyStore block should have encrypted flag set.");
        Assert.Equal(encryptedPayload, readBlock.Payload);
    }

    [Fact]
    public async Task FileCreation_DecryptedKeyStore_HasActiveEpoch0()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        Assert.True(readResult.IsSuccess);

        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.Equal(0, decrypted.ActiveEpoch);
    }

    [Fact]
    public async Task FileCreation_DecryptedKeyStore_ContainsSingleEpoch0Entry()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        Assert.True(readResult.IsSuccess);

        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.Single(decrypted.Entries);
        Assert.Equal(0, decrypted.Entries[0].Epoch);
    }

    [Fact]
    public async Task FileCreation_Epoch0Dek_Is32Bytes()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.Equal(KeySize, decrypted.Entries[0].DEK.Length);
        Assert.False(decrypted.Entries[0].DEK.All(b => b == 0),
            "DEK should be randomly generated, not all zeros.");
    }

    [Fact]
    public async Task FileCreation_Epoch0Dek_MatchesOriginal()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.Equal(content.Entries[0].DEK, decrypted.Entries[0].DEK);
    }

    [Fact]
    public async Task FileCreation_Epoch0Entry_IsNotRetired()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.False(decrypted.Entries[0].Retired,
            "Initial epoch 0 entry should not be retired on file creation.");
    }

    [Fact]
    public async Task FileCreation_Epoch0Entry_HasTimestamp()
    {
        var beforeCreation = DateTime.UtcNow.AddSeconds(-1);
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();
        var afterCreation = DateTime.UtcNow.AddSeconds(1);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);
        await rbm.WriteBlockAsync(block);

        var readResult = await rbm.ReadBlockAsync(1);
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.True(decrypted.Entries[0].Timestamp >= beforeCreation,
            "Epoch 0 timestamp should be at or after test start.");
        Assert.True(decrypted.Entries[0].Timestamp <= afterCreation,
            "Epoch 0 timestamp should be at or before test end.");
    }

    [Fact]
    public async Task FileCreation_KeyStoreBlockPersists_AcrossReopen()
    {
        var (content, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        // Write and close
        using (var rbm = new RawBlockManager(_tempFile))
        {
            var writeResult = await rbm.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);
        }

        // Reopen and read
        using (var rbm2 = new RawBlockManager(_tempFile, createIfNotExists: false))
        {
            var readResult = await rbm2.ReadBlockAsync(1);
            Assert.True(readResult.IsSuccess, "KeyStore block should survive file close/reopen.");

            var serializer = new DefaultBlockContentSerializer();
            var ksManager = new KeyStoreManager(serializer);
            var decrypted = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

            Assert.Equal(0, decrypted.ActiveEpoch);
            Assert.Single(decrypted.Entries);
            Assert.Equal(0, decrypted.Entries[0].Epoch);
            Assert.Equal(content.Entries[0].DEK, decrypted.Entries[0].DEK);
            Assert.False(decrypted.Entries[0].Retired);
        }
    }

    [Fact]
    public async Task FileCreation_NewFileSizeGrowsAfterKeyStoreWrite()
    {
        var (_, kek, encryptedPayload) = CreateInitialKeyStore();

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using var rbm = new RawBlockManager(_tempFile);

        // New file should start empty
        var initialLength = rbm.FileLength;

        await rbm.WriteBlockAsync(block);

        Assert.True(rbm.FileLength > initialLength,
            "File size should increase after writing the KeyStore block.");
    }
}
