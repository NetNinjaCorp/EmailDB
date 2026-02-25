using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class RoundTripMultipleDeksTests
{
    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    [Fact]
    public void RoundTrip_EncryptWithEpoch0_DecryptWithEpoch0_ProducesCorrectPlaintext()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Data encrypted under epoch 0"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 0);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_MultipleEpochs_EachDecryptsToCorrectPlaintext()
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

        // Simulate data encrypted at different epochs using standalone providers
        using var s0 = new AesGcmBlockEncryptionProvider(dek0);
        using var s1 = new AesGcmBlockEncryptionProvider(dek1);
        using var s2 = new AesGcmBlockEncryptionProvider(dek2);

        var plain0 = "Email body from epoch 0"u8.ToArray();
        var plain1 = "Email body from epoch 1"u8.ToArray();
        var plain2 = "Email body from epoch 2"u8.ToArray();

        var ct0 = s0.Encrypt(plain0, BlockType.EmailContent, blockId: 10);
        var ct1 = s1.Encrypt(plain1, BlockType.EmailContent, blockId: 11);
        var ct2 = s2.Encrypt(plain2, BlockType.EmailContent, blockId: 12);

        // Each epoch decrypts to its own correct plaintext
        Assert.Equal(plain0, provider.Decrypt(ct0, BlockType.EmailContent, blockId: 10, keyEpoch: 0));
        Assert.Equal(plain1, provider.Decrypt(ct1, BlockType.EmailContent, blockId: 11, keyEpoch: 1));
        Assert.Equal(plain2, provider.Decrypt(ct2, BlockType.EmailContent, blockId: 12, keyEpoch: 2));
    }

    [Fact]
    public void RoundTrip_EncryptThenRotateKey_OldDataStillDecrypts()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        // Phase 1: only epoch 0 exists
        var keyStoreV1 = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        byte[] ciphertextFromEpoch0;
        var plaintext = "Written before key rotation"u8.ToArray();

        using (var providerV1 = new KeyWrappingEncryptionProvider(keyStoreV1, EncryptionPolicy.Default))
        {
            ciphertextFromEpoch0 = providerV1.Encrypt(plaintext, BlockType.EmailContent, blockId: 50);
        }

        // Phase 2: key rotation — epoch 1 is now active, epoch 0 is retired
        var keyStoreV2 = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var providerV2 = new KeyWrappingEncryptionProvider(keyStoreV2, EncryptionPolicy.Default);

        // Old data encrypted under epoch 0 still decrypts correctly
        var decrypted = providerV2.Decrypt(ciphertextFromEpoch0, BlockType.EmailContent, blockId: 50, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);

        // New data encrypted under epoch 1 also round-trips
        var newPlaintext = "Written after key rotation"u8.ToArray();
        var newCiphertext = providerV2.Encrypt(newPlaintext, BlockType.EmailContent, blockId: 51);
        var newDecrypted = providerV2.Decrypt(newCiphertext, BlockType.EmailContent, blockId: 51, keyEpoch: 1);
        Assert.Equal(newPlaintext, newDecrypted);
    }

    [Fact]
    public void RoundTrip_FiveEpochs_AllProduceCorrectPlaintext()
    {
        var deks = Enumerable.Range(0, 5).Select(_ => GenerateDek()).ToArray();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 4,
            Entries = deks.Select((dek, i) => new KeyStoreEntry
            {
                Epoch = i,
                DEK = dek,
                Timestamp = DateTime.UtcNow,
                Retired = i < 4
            }).ToList()
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        for (int epoch = 0; epoch < 5; epoch++)
        {
            using var standalone = new AesGcmBlockEncryptionProvider(deks[epoch]);
            var plaintext = System.Text.Encoding.UTF8.GetBytes($"Payload for epoch {epoch}");
            var ciphertext = standalone.Encrypt(plaintext, BlockType.EmailContent, blockId: epoch + 100);

            var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: epoch + 100, keyEpoch: epoch);
            Assert.Equal(plaintext, decrypted);
        }
    }

    [Fact]
    public void RoundTrip_SamePlaintext_DifferentEpochs_ProducesDifferentCiphertext()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var plaintext = "Identical content"u8.ToArray();

        // Encrypt same plaintext with each epoch's standalone provider
        using var s0 = new AesGcmBlockEncryptionProvider(dek0);
        using var s1 = new AesGcmBlockEncryptionProvider(dek1);
        var ct0 = s0.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var ct1 = s1.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Ciphertexts must differ (different keys)
        Assert.NotEqual(ct0, ct1);

        // But both decrypt to the same plaintext via the wrapping provider
        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        Assert.Equal(plaintext, provider.Decrypt(ct0, BlockType.EmailContent, blockId: 1, keyEpoch: 0));
        Assert.Equal(plaintext, provider.Decrypt(ct1, BlockType.EmailContent, blockId: 1, keyEpoch: 1));
    }

    [Fact]
    public void RoundTrip_LargePayload_WithMultipleDeks_ProducesCorrectPlaintext()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // 64 KB payload
        var plaintext = new byte[65536];
        RandomNumberGenerator.Fill(plaintext);

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 999);
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 999, keyEpoch: 1);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_EmptyPayload_WithMultipleDeks_ProducesCorrectPlaintext()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = Array.Empty<byte>();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 1);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RoundTrip_MultipleBlockTypes_WithMultipleDeks_AllProduceCorrectPlaintext()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        using var s0 = new AesGcmBlockEncryptionProvider(dek0);

        var blockTypes = new[] { BlockType.EmailContent, BlockType.Folder, BlockType.FolderTree, BlockType.Segment, BlockType.WAL };

        long blockId = 200;
        foreach (var bt in blockTypes)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes($"Content for {bt}");

            // Data encrypted with retired epoch 0
            var ct0 = s0.Encrypt(plaintext, bt, blockId);
            Assert.Equal(plaintext, provider.Decrypt(ct0, bt, blockId, keyEpoch: 0));

            // Data encrypted with active epoch 1
            var ct1 = provider.Encrypt(plaintext, bt, blockId + 1);
            Assert.Equal(plaintext, provider.Decrypt(ct1, bt, blockId + 1, keyEpoch: 1));

            blockId += 10;
        }
    }

    [Fact]
    public void RoundTrip_CrossEpochDecryptionDoesNotCorrupt()
    {
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        using var s0 = new AesGcmBlockEncryptionProvider(dek0);

        var plaintext = "Cross-epoch integrity check"u8.ToArray();
        var ct = s0.Encrypt(plaintext, BlockType.EmailContent, blockId: 77);

        // Correct epoch succeeds
        var decrypted = provider.Decrypt(ct, BlockType.EmailContent, blockId: 77, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);

        // Wrong epoch throws — does not silently return garbage
        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(ct, BlockType.EmailContent, blockId: 77, keyEpoch: 1));
    }
}
