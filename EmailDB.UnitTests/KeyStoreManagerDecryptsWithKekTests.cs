using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that KeyStoreManager decrypts key store with KEK and loads DEK table.
/// </summary>
public class KeyStoreManagerDecryptsWithKekTests
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static byte[] GenerateKek()
    {
        var kek = new byte[KeySize];
        RandomNumberGenerator.Fill(kek);
        return kek;
    }

    private static KeyStoreContent CreateSampleContent()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);

        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new()
                {
                    Epoch = 0,
                    DEK = dek,
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Retired = false
                }
            }
        };
    }

    [Fact]
    public void DecryptKeyStore_RoundTrip_RestoresActiveEpoch()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.ActiveEpoch, decrypted.ActiveEpoch);
    }

    [Fact]
    public void DecryptKeyStore_RoundTrip_RestoresEntryCount()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.Entries.Count, decrypted.Entries.Count);
    }

    [Fact]
    public void DecryptKeyStore_RoundTrip_RestoresDekBytes()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.Entries[0].DEK, decrypted.Entries[0].DEK);
    }

    [Fact]
    public void DecryptKeyStore_RoundTrip_RestoresEpoch()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.Entries[0].Epoch, decrypted.Entries[0].Epoch);
    }

    [Fact]
    public void DecryptKeyStore_RoundTrip_RestoresTimestamp()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.Entries[0].Timestamp, decrypted.Entries[0].Timestamp);
    }

    [Fact]
    public void DecryptKeyStore_RoundTrip_RestoresRetiredFlag()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.Entries[0].Retired, decrypted.Entries[0].Retired);
    }

    [Fact]
    public void DecryptKeyStore_MultipleEntries_LoadsFullDekTable()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        var dek0 = new byte[32];
        var dek1 = new byte[32];
        var dek2 = new byte[32];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);
        RandomNumberGenerator.Fill(dek2);

        var content = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false },
            }
        };

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(3, decrypted.Entries.Count);
        Assert.Equal(2, decrypted.ActiveEpoch);

        for (int i = 0; i < content.Entries.Count; i++)
        {
            Assert.Equal(content.Entries[i].Epoch, decrypted.Entries[i].Epoch);
            Assert.Equal(content.Entries[i].DEK, decrypted.Entries[i].DEK);
            Assert.Equal(content.Entries[i].Timestamp, decrypted.Entries[i].Timestamp);
            Assert.Equal(content.Entries[i].Retired, decrypted.Entries[i].Retired);
        }
    }

    [Fact]
    public void DecryptKeyStore_MultipleEntries_EachDekIsDistinct()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        var dek0 = new byte[32];
        var dek1 = new byte[32];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);

        var content = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddDays(-30), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false },
            }
        };

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.NotEqual(decrypted.Entries[0].DEK, decrypted.Entries[1].DEK);
    }

    [Fact]
    public void DecryptKeyStore_EmptyEntries_ReturnsEmptyDekTable()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        var content = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>()
        };

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Empty(decrypted.Entries);
        Assert.Equal(0, decrypted.ActiveEpoch);
    }

    [Fact]
    public void DecryptKeyStore_WrongKek_ThrowsCryptographicException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var wrongKek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, wrongKek));
    }

    [Fact]
    public void DecryptKeyStore_NullEncryptedData_ThrowsArgumentNullException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        Assert.Throws<ArgumentNullException>(() =>
            manager.DecryptKeyStore(null!, kek));
    }

    [Fact]
    public void DecryptKeyStore_NullKek_ThrowsArgumentException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var content = CreateSampleContent();
        var kek = GenerateKek();

        var encrypted = manager.EncryptKeyStore(content, kek);

        Assert.Throws<ArgumentException>(() =>
            manager.DecryptKeyStore(encrypted, null!));
    }

    [Fact]
    public void DecryptKeyStore_WrongKekSize_ThrowsArgumentException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var content = CreateSampleContent();
        var kek = GenerateKek();

        var encrypted = manager.EncryptKeyStore(content, kek);

        Assert.Throws<ArgumentException>(() =>
            manager.DecryptKeyStore(encrypted, new byte[16]));
    }

    [Fact]
    public void DecryptKeyStore_TooShortInput_ThrowsCryptographicException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        // Less than NonceSize + TagSize bytes
        var tooShort = new byte[NonceSize + TagSize - 1];

        Assert.Throws<CryptographicException>(() =>
            manager.DecryptKeyStore(tooShort, kek));
    }

    [Fact]
    public void DecryptKeyStore_TamperedCiphertext_ThrowsCryptographicException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Flip a byte in the ciphertext region
        encrypted[NonceSize + 1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, kek));
    }

    [Fact]
    public void DecryptKeyStore_TamperedAuthTag_ThrowsCryptographicException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Flip a byte in the auth tag
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, kek));
    }

    [Fact]
    public void DecryptKeyStore_TamperedNonce_ThrowsCryptographicException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Flip a byte in the nonce
        encrypted[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, kek));
    }

    [Fact]
    public void DecryptKeyStore_EncryptedWithRawAesGcm_CanBeDecryptedByManager()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var content = CreateSampleContent();

        // Manually encrypt with raw AesGcm
        var plaintext = serializer.Serialize(content);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Assemble the same format: Nonce + Ciphertext + Tag
        var encrypted = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(encrypted.AsSpan(0));
        ciphertext.CopyTo(encrypted.AsSpan(NonceSize));
        tag.CopyTo(encrypted.AsSpan(NonceSize + ciphertext.Length));

        // DecryptKeyStore should handle it
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(content.ActiveEpoch, decrypted.ActiveEpoch);
        Assert.Single(decrypted.Entries);
        Assert.Equal(content.Entries[0].DEK, decrypted.Entries[0].DEK);
    }

    [Fact]
    public void DecryptKeyStore_RetiredEntriesPreserved()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);

        var content = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek, Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
            }
        };

        var encrypted = manager.EncryptKeyStore(content, kek);
        var decrypted = manager.DecryptKeyStore(encrypted, kek);

        Assert.True(decrypted.Entries[0].Retired);
    }
}
