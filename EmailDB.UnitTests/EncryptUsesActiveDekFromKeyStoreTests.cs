using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class EncryptUsesActiveDekFromKeyStoreTests
{
    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    [Fact]
    public void ActiveEpoch_ReturnsKeyStoreActiveEpoch()
    {
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 3,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 1, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 2, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 3, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        Assert.Equal(3, provider.ActiveEpoch);
    }

    [Fact]
    public void Encrypt_ProducesCiphertextDecryptableByActiveDek()
    {
        var activeDek = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = activeDek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Hello, encrypted world!"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Decrypt with a standalone AesGcm provider using the same active DEK
        using var standalone = new AesGcmBlockEncryptionProvider(activeDek);
        var decrypted = standalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_UsesActiveDekNotRetiredDek()
    {
        var retiredDek = GenerateDek();
        var activeDek = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = retiredDek, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = activeDek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Test data"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 42);

        // The active DEK (epoch 1) should be able to decrypt it
        using var activeStandalone = new AesGcmBlockEncryptionProvider(activeDek);
        var decrypted = activeStandalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 42);
        Assert.Equal(plaintext, decrypted);

        // The retired DEK (epoch 0) should NOT be able to decrypt it
        using var retiredStandalone = new AesGcmBlockEncryptionProvider(retiredDek);
        Assert.ThrowsAny<CryptographicException>(() =>
            retiredStandalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 42));
    }

    [Fact]
    public void Encrypt_WithMultipleEpochs_AlwaysUsesActiveDek()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var dek2 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Encrypt multiple blocks
        for (long blockId = 1; blockId <= 5; blockId++)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes($"Block {blockId} data");
            var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

            // All should be decryptable with the active DEK (epoch 2)
            using var standalone = new AesGcmBlockEncryptionProvider(dek2);
            var decrypted = standalone.Decrypt(ciphertext, BlockType.EmailContent, blockId);
            Assert.Equal(plaintext, decrypted);
        }
    }

    [Fact]
    public void Encrypt_CiphertextDiffersFromPlaintext()
    {
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Sensitive email content"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Ciphertext should be different from plaintext
        Assert.NotEqual(plaintext, ciphertext);
        // Ciphertext should include overhead (12 nonce + 16 tag = 28 bytes)
        Assert.Equal(plaintext.Length + 28, ciphertext.Length);
    }

    [Fact]
    public void Encrypt_ThenDecryptViaProvider_RoundTrips()
    {
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Round trip test"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 7);
        // Decrypt using the provider itself (should use the active epoch internally)
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 7);

        Assert.Equal(plaintext, decrypted);
    }
}
