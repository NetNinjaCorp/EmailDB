using System.Security.Cryptography;
using System.Text;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-53:
/// "On file open KEK derived from password decrypts key store and loads DEK table"
///
/// Simulates the full file-creation → close → reopen flow:
///   1. Derive KEK from password + salt (Argon2id)
///   2. Create key store with DEK table, encrypt with KEK
///   3. Write encryption header (salt) and key store block to file
///   4. Close file
///   5. Reopen file, read encryption header to recover salt
///   6. Re-derive KEK from same password + recovered salt
///   7. Read key store block, decrypt with re-derived KEK
///   8. Verify DEK table is fully loaded
/// </summary>
public class FileOpenKekDecryptsKeyStoreTests : IDisposable
{
    private const int KeySize = 32;
    private const string TestPassword = "correct-horse-battery-staple";
    private readonly string _tempBlockFile;
    private readonly string _tempHeaderFile;

    public FileOpenKekDecryptsKeyStoreTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _tempBlockFile = Path.Combine(Path.GetTempPath(), $"emdb_blocks_{id}.emdb");
        _tempHeaderFile = Path.Combine(Path.GetTempPath(), $"emdb_header_{id}.emdb");
    }

    public void Dispose()
    {
        if (File.Exists(_tempBlockFile))
            File.Delete(_tempBlockFile);
        if (File.Exists(_tempHeaderFile))
            File.Delete(_tempHeaderFile);
    }

    /// <summary>
    /// Simulates file creation: derive KEK, build key store, encrypt, write header + block.
    /// Returns the original DEK for later verification.
    /// </summary>
    private async Task<(KeyStoreContent originalContent, byte[] originalDek)> SimulateFileCreation()
    {
        // 1. Generate salt and derive KEK from password
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveFromPassword(TestPassword, salt);

        // 2. Build encryption header with salt and key verification token
        using var encProvider = new AesGcmBlockEncryptionProvider(kek);
        var verificationToken = encProvider.Encrypt(
            Encoding.ASCII.GetBytes("EMDB"), BlockType.Metadata, blockId: 0);

        var header = new EncryptionHeader
        {
            Salt = salt,
            KeyVerificationToken = verificationToken
        };

        // Write header to its own file (simulating file-level header)
        using (var headerStream = new FileStream(_tempHeaderFile, FileMode.Create, FileAccess.Write))
        {
            EncryptionHeaderManager.WriteHeader(headerStream, header);
        }

        // 3. Create initial DEK table with epoch 0
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

        // 4. Encrypt key store with KEK and write block
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var encryptedPayload = ksManager.EncryptKeyStore(content, kek);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using (var rbm = new RawBlockManager(_tempBlockFile))
        {
            var writeResult = await rbm.WriteBlockAsync(block);
            if (!writeResult.IsSuccess)
                throw new InvalidOperationException($"File creation failed: {writeResult.Error}");
        }

        return (content, dek);
    }

    /// <summary>
    /// Simulates file open: read header, re-derive KEK, read and decrypt key store.
    /// </summary>
    private async Task<KeyStoreContent> SimulateFileOpen()
    {
        // 1. Read encryption header to recover salt
        EncryptionHeader header;
        using (var headerStream = new FileStream(_tempHeaderFile, FileMode.Open, FileAccess.Read))
        {
            var headerResult = EncryptionHeaderManager.ReadHeader(headerStream);
            if (!headerResult.IsSuccess)
                throw new InvalidOperationException($"Header read failed: {headerResult.Error}");
            header = headerResult.Value;
        }

        // 2. Re-derive KEK from password + salt
        var kek = KeyDerivation.DeriveFromPassword(TestPassword, header.Salt);

        // 3. Read key store block from file
        using var rbm = new RawBlockManager(_tempBlockFile, createIfNotExists: false);
        var readResult = await rbm.ReadBlockAsync(1);
        if (!readResult.IsSuccess)
            throw new InvalidOperationException($"Block read failed: {readResult.Error}");

        // 4. Decrypt key store with re-derived KEK
        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        return ksManager.DecryptKeyStore(readResult.Value.Payload, kek);
    }

    [Fact]
    public async Task FileOpen_ReDerivedKek_DecryptsKeyStore()
    {
        await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task FileOpen_LoadedDekTable_HasCorrectActiveEpoch()
    {
        await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.Equal(0, loaded.ActiveEpoch);
    }

    [Fact]
    public async Task FileOpen_LoadedDekTable_ContainsSingleEntry()
    {
        await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.Single(loaded.Entries);
    }

    [Fact]
    public async Task FileOpen_LoadedDekTable_EntryHasEpoch0()
    {
        await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.Equal(0, loaded.Entries[0].Epoch);
    }

    [Fact]
    public async Task FileOpen_LoadedDek_Is32Bytes()
    {
        await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.Equal(KeySize, loaded.Entries[0].DEK.Length);
    }

    [Fact]
    public async Task FileOpen_LoadedDek_MatchesOriginalDek()
    {
        var (_, originalDek) = await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.Equal(originalDek, loaded.Entries[0].DEK);
    }

    [Fact]
    public async Task FileOpen_LoadedEntry_IsNotRetired()
    {
        await SimulateFileCreation();
        var loaded = await SimulateFileOpen();

        Assert.False(loaded.Entries[0].Retired,
            "Epoch 0 entry should not be retired after file open.");
    }

    [Fact]
    public async Task FileOpen_WrongPassword_FailsToDecryptKeyStore()
    {
        await SimulateFileCreation();

        // Read header to get salt
        EncryptionHeader header;
        using (var headerStream = new FileStream(_tempHeaderFile, FileMode.Open, FileAccess.Read))
        {
            var headerResult = EncryptionHeaderManager.ReadHeader(headerStream);
            header = headerResult.Value;
        }

        // Derive KEK from wrong password
        var wrongKek = KeyDerivation.DeriveFromPassword("wrong-password-attempt", header.Salt);

        using var rbm = new RawBlockManager(_tempBlockFile, createIfNotExists: false);
        var readResult = await rbm.ReadBlockAsync(1);
        Assert.True(readResult.IsSuccess);

        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(readResult.Value.Payload, wrongKek));
    }

    [Fact]
    public async Task FileOpen_MultiEpochDekTable_LoadsAllEntries()
    {
        // Create a key store with multiple epochs
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveFromPassword(TestPassword, salt);

        // Write encryption header
        using var encProvider = new AesGcmBlockEncryptionProvider(kek);
        var verificationToken = encProvider.Encrypt(
            Encoding.ASCII.GetBytes("EMDB"), BlockType.Metadata, blockId: 0);

        var header = new EncryptionHeader
        {
            Salt = salt,
            KeyVerificationToken = verificationToken
        };

        using (var headerStream = new FileStream(_tempHeaderFile, FileMode.Create, FileAccess.Write))
        {
            EncryptionHeaderManager.WriteHeader(headerStream, header);
        }

        // Build multi-epoch DEK table
        var dek0 = new byte[KeySize];
        var dek1 = new byte[KeySize];
        var dek2 = new byte[KeySize];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);
        RandomNumberGenerator.Fill(dek2);

        var content = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddHours(-2), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow.AddHours(-1), Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var encryptedPayload = ksManager.EncryptKeyStore(content, kek);

        var block = new Block
        {
            Version = 1,
            Type = BlockType.KeyStore,
            Flags = Block.FlagEncrypted,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockId = 1,
            Payload = encryptedPayload
        };

        using (var rbm = new RawBlockManager(_tempBlockFile))
        {
            await rbm.WriteBlockAsync(block);
        }

        // Simulate file open
        var loaded = await SimulateFileOpen();

        Assert.Equal(3, loaded.Entries.Count);
        Assert.Equal(2, loaded.ActiveEpoch);
        Assert.Equal(dek0, loaded.Entries[0].DEK);
        Assert.Equal(dek1, loaded.Entries[1].DEK);
        Assert.Equal(dek2, loaded.Entries[2].DEK);
        Assert.True(loaded.Entries[0].Retired);
        Assert.True(loaded.Entries[1].Retired);
        Assert.False(loaded.Entries[2].Retired);
    }

    [Fact]
    public async Task FileOpen_KeyVerificationToken_ValidatesBeforeKeyStoreDecrypt()
    {
        await SimulateFileCreation();

        // Read header
        EncryptionHeader header;
        using (var headerStream = new FileStream(_tempHeaderFile, FileMode.Open, FileAccess.Read))
        {
            var headerResult = EncryptionHeaderManager.ReadHeader(headerStream);
            header = headerResult.Value;
        }

        // Re-derive correct KEK
        var kek = KeyDerivation.DeriveFromPassword(TestPassword, header.Salt);

        // Verify the key verification token decrypts to "EMDB"
        using var provider = new AesGcmBlockEncryptionProvider(kek);
        var decryptedToken = provider.Decrypt(header.KeyVerificationToken, BlockType.Metadata, blockId: 0);
        Assert.Equal(EncryptionHeader.MagicBytes, decryptedToken);

        // Then decrypt key store succeeds
        using var rbm = new RawBlockManager(_tempBlockFile, createIfNotExists: false);
        var readResult = await rbm.ReadBlockAsync(1);

        var serializer = new DefaultBlockContentSerializer();
        var ksManager = new KeyStoreManager(serializer);
        var loaded = ksManager.DecryptKeyStore(readResult.Value.Payload, kek);

        Assert.Equal(0, loaded.ActiveEpoch);
        Assert.Single(loaded.Entries);
    }

    [Fact]
    public async Task FileOpen_SamePasswordSameSalt_ProducesSameKek()
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek1 = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        var kek2 = KeyDerivation.DeriveFromPassword(TestPassword, salt);

        Assert.Equal(kek1, kek2);
    }
}
