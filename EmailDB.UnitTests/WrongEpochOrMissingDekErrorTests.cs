using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that decrypting with a wrong key epoch or a missing DEK returns
/// a clear CryptographicException with a descriptive message.
/// Acceptance criterion: "Wrong epoch or missing DEK returns clear error" (US-EMDB-55).
/// </summary>
public class WrongEpochOrMissingDekErrorTests
{
    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    [Fact]
    public void Decrypt_MissingEpoch_ThrowsCryptographicException()
    {
        var dek0 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "test data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Epoch 99 does not exist in the key store
        var ex = Assert.Throws<CryptographicException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 99));

        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void Decrypt_MissingEpoch_MessageMentionsEpochNumber()
    {
        var dek0 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "payload"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 2);

        var ex = Assert.Throws<CryptographicException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 2, keyEpoch: 42));

        // The error message should clearly identify the missing epoch
        Assert.Contains("42", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message),
            "Error message must not be empty.");
    }

    [Fact]
    public void Decrypt_WrongEpoch_DekExists_ThrowsCryptographicException()
    {
        // Both epochs exist, but we decrypt with the wrong one (key mismatch)
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
        var plaintext = "encrypted with epoch 1"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 10);

        // Epoch 0 exists but its DEK is different — auth tag mismatch
        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 10, keyEpoch: 0));
    }

    [Fact]
    public void Decrypt_WrongEpoch_DoesNotReturnGarbageData()
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
        var plaintext = "must not silently corrupt"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 11);

        bool threw = false;
        try
        {
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 11, keyEpoch: 0);
        }
        catch (CryptographicException)
        {
            threw = true;
        }

        Assert.True(threw,
            "Decrypt with wrong epoch must throw, not silently return incorrect data.");
    }

    [Fact]
    public void Decrypt_NegativeEpoch_ThrowsCryptographicException()
    {
        var dek0 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "test"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 12);

        Assert.Throws<CryptographicException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 12, keyEpoch: -1));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void Decrypt_VariousMissingEpochs_AllThrowWithEpochInMessage(int missingEpoch)
    {
        var dek0 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 13);

        var ex = Assert.Throws<CryptographicException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 13, keyEpoch: missingEpoch));

        Assert.Contains(missingEpoch.ToString(), ex.Message);
    }

    [Fact]
    public void Decrypt_MissingEpoch_ErrorIsCryptographicException_NotKeyNotFoundException()
    {
        // Missing epoch must raise a crypto-domain error, not a generic dictionary exception
        var dek0 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "payload"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 14);

        var exception = Record.Exception(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 14, keyEpoch: 5));

        Assert.NotNull(exception);
        Assert.IsType<CryptographicException>(exception);
        Assert.IsNotType<KeyNotFoundException>(exception);
    }

    [Fact]
    public void Constructor_ActiveEpochMissing_ThrowsArgumentException()
    {
        // If the key store claims active epoch 5 but has no entry for it,
        // construction must fail fast with a clear error
        var dek0 = GenerateDek();
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 5,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default));

        Assert.Contains("5", ex.Message);
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.Segment)]
    public void Decrypt_WrongEpoch_AllEncryptableBlockTypes_ThrowsCryptographicException(BlockType blockType)
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
        var plaintext = "block data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, blockType, blockId: 20);

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(ciphertext, blockType, blockId: 20, keyEpoch: 0));
    }
}
