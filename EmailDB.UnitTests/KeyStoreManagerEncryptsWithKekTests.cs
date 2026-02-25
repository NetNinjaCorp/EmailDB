using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that KeyStoreManager encrypts key store payload with KEK using AES-256-GCM.
/// </summary>
public class KeyStoreManagerEncryptsWithKekTests
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
    public void EncryptKeyStore_ReturnsNonEmptyEncryptedPayload()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        Assert.NotNull(encrypted);
        Assert.True(encrypted.Length > NonceSize + TagSize,
            "Encrypted output must contain nonce + ciphertext + auth tag.");
    }

    [Fact]
    public void EncryptKeyStore_OutputFormat_ContainsNonceCiphertextAndTag()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var serialized = serializer.Serialize(content);
        var encrypted = manager.EncryptKeyStore(content, kek);

        // Output format: Nonce(12) + Ciphertext(serialized.Length) + AuthTag(16)
        Assert.Equal(NonceSize + serialized.Length + TagSize, encrypted.Length);
    }

    [Fact]
    public void EncryptKeyStore_CiphertextDiffersFromPlaintext()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var serialized = serializer.Serialize(content);
        var encrypted = manager.EncryptKeyStore(content, kek);

        // Extract ciphertext portion (skip nonce, exclude tag)
        var ciphertextPortion = encrypted.AsSpan(NonceSize, serialized.Length).ToArray();

        Assert.NotEqual(serialized, ciphertextPortion);
    }

    [Fact]
    public void EncryptKeyStore_TwoEncryptionsOfSameContent_ProduceDifferentCiphertext()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted1 = manager.EncryptKeyStore(content, kek);
        var encrypted2 = manager.EncryptKeyStore(content, kek);

        // Due to random nonce, ciphertexts should differ
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void EncryptKeyStore_NonceIs12Bytes_FromStartOfOutput()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Nonce is first 12 bytes; verify they are not all zeros (random fill)
        var nonce = encrypted.AsSpan(0, NonceSize).ToArray();
        Assert.False(nonce.All(b => b == 0), "Nonce should be randomly generated, not all zeros.");
    }

    [Fact]
    public void EncryptKeyStore_WrongKekSize_ThrowsArgumentException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var content = CreateSampleContent();

        Assert.Throws<ArgumentException>(() =>
            manager.EncryptKeyStore(content, new byte[16])); // AES-128 key, not AES-256
    }

    [Fact]
    public void EncryptKeyStore_NullKek_ThrowsArgumentException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var content = CreateSampleContent();

        Assert.Throws<ArgumentException>(() =>
            manager.EncryptKeyStore(content, null!));
    }

    [Fact]
    public void EncryptKeyStore_NullContent_ThrowsArgumentNullException()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();

        Assert.Throws<ArgumentNullException>(() =>
            manager.EncryptKeyStore(null!, kek));
    }

    [Fact]
    public void EncryptKeyStore_MultipleEntries_ProducesValidEncryptedPayload()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();

        var content = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = new byte[32], Timestamp = DateTime.UtcNow.AddDays(-60), Retired = true },
                new() { Epoch = 1, DEK = new byte[32], Timestamp = DateTime.UtcNow.AddDays(-30), Retired = true },
                new() { Epoch = 2, DEK = new byte[32], Timestamp = DateTime.UtcNow, Retired = false },
            }
        };
        RandomNumberGenerator.Fill(content.Entries[0].DEK);
        RandomNumberGenerator.Fill(content.Entries[1].DEK);
        RandomNumberGenerator.Fill(content.Entries[2].DEK);

        var serialized = serializer.Serialize(content);
        var encrypted = manager.EncryptKeyStore(content, kek);

        Assert.Equal(NonceSize + serialized.Length + TagSize, encrypted.Length);
    }

    [Fact]
    public void EncryptKeyStore_UsesAes256Gcm_DecryptableWithRawAesGcm()
    {
        var serializer = new DefaultBlockContentSerializer();
        var manager = new KeyStoreManager(serializer);
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Manually decrypt with raw AesGcm to prove it's real AES-256-GCM
        var nonce = encrypted.AsSpan(0, NonceSize);
        var ciphertext = encrypted.AsSpan(NonceSize, encrypted.Length - NonceSize - TagSize);
        var tag = encrypted.AsSpan(encrypted.Length - TagSize, TagSize);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        // Deserialize and verify
        var deserialized = serializer.Deserialize<KeyStoreContent>(plaintext);
        Assert.Equal(content.ActiveEpoch, deserialized.ActiveEpoch);
        Assert.Single(deserialized.Entries);
        Assert.Equal(content.Entries[0].Epoch, deserialized.Entries[0].Epoch);
        Assert.Equal(content.Entries[0].DEK, deserialized.Entries[0].DEK);
    }

    [Fact]
    public void EncryptKeyStore_WrongKek_FailsDecryption()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var wrongKek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Attempting to decrypt with wrong KEK should throw (AuthenticationTagMismatchException derives from CryptographicException)
        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, wrongKek));
    }

    [Fact]
    public void EncryptKeyStore_TamperedCiphertext_FailsDecryption()
    {
        var manager = new KeyStoreManager(new DefaultBlockContentSerializer());
        var kek = GenerateKek();
        var content = CreateSampleContent();

        var encrypted = manager.EncryptKeyStore(content, kek);

        // Tamper with a ciphertext byte (after nonce, before tag)
        encrypted[NonceSize + 1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            manager.DecryptKeyStore(encrypted, kek));
    }
}
