using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class DecryptAcceptsKeyEpochLooksUpDekTests
{
    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    [Fact]
    public void Decrypt_WithExplicitEpoch_UsesCorrectDek()
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

        // Encrypt with a standalone provider using epoch 0's DEK
        using var standalone0 = new AesGcmBlockEncryptionProvider(dek0);
        var plaintext = "Data encrypted with old key"u8.ToArray();
        var ciphertext = standalone0.Encrypt(plaintext, BlockType.EmailContent, blockId: 10);

        // Decrypt via KeyWrapping provider specifying epoch 0
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 10, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WithRetiredEpoch_StillDecryptsSuccessfully()
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

        // Encrypt with standalone providers for each retired epoch
        using var standalone0 = new AesGcmBlockEncryptionProvider(dek0);
        using var standalone1 = new AesGcmBlockEncryptionProvider(dek1);

        var plaintext0 = "Old data from epoch 0"u8.ToArray();
        var plaintext1 = "Old data from epoch 1"u8.ToArray();

        var ciphertext0 = standalone0.Encrypt(plaintext0, BlockType.EmailContent, blockId: 100);
        var ciphertext1 = standalone1.Encrypt(plaintext1, BlockType.EmailContent, blockId: 101);

        // Both retired epochs should still decrypt correctly
        Assert.Equal(plaintext0, provider.Decrypt(ciphertext0, BlockType.EmailContent, blockId: 100, keyEpoch: 0));
        Assert.Equal(plaintext1, provider.Decrypt(ciphertext1, BlockType.EmailContent, blockId: 101, keyEpoch: 1));
    }

    [Fact]
    public void Decrypt_WithActiveEpoch_DecryptsCorrectly()
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
        var plaintext = "Active epoch data"u8.ToArray();

        // Encrypt with the provider (uses active epoch)
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 5);

        // Decrypt with explicit active epoch should work
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 5, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WithoutEpochParameter_DefaultsToActiveEpoch()
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
        var plaintext = "Default epoch test"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 3);

        // No-epoch overload should use active epoch internally
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 3);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WrongEpoch_ThrowsCryptographicException()
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
        var plaintext = "Mismatch test"u8.ToArray();

        // Encrypt with epoch 1 (active)
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 20);

        // Trying to decrypt with epoch 0 should fail (wrong key)
        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 20, keyEpoch: 0));
    }

    [Fact]
    public void Decrypt_MultipleBlocks_EachWithDifferentEpoch()
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

        // Simulate blocks encrypted at different key epochs
        using var s0 = new AesGcmBlockEncryptionProvider(dek0);
        using var s1 = new AesGcmBlockEncryptionProvider(dek1);
        using var s2 = new AesGcmBlockEncryptionProvider(dek2);

        var data0 = "Email from epoch 0"u8.ToArray();
        var data1 = "Email from epoch 1"u8.ToArray();
        var data2 = "Email from epoch 2"u8.ToArray();

        var ct0 = s0.Encrypt(data0, BlockType.EmailContent, blockId: 1);
        var ct1 = s1.Encrypt(data1, BlockType.EmailContent, blockId: 2);
        var ct2 = s2.Encrypt(data2, BlockType.EmailContent, blockId: 3);

        // Decrypt each block with the correct epoch
        Assert.Equal(data0, provider.Decrypt(ct0, BlockType.EmailContent, blockId: 1, keyEpoch: 0));
        Assert.Equal(data1, provider.Decrypt(ct1, BlockType.EmailContent, blockId: 2, keyEpoch: 1));
        Assert.Equal(data2, provider.Decrypt(ct2, BlockType.EmailContent, blockId: 3, keyEpoch: 2));
    }

    [Fact]
    public void Decrypt_WorksAcrossBlockTypes()
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
        using var standalone0 = new AesGcmBlockEncryptionProvider(dek0);

        // Encrypt different block types with the retired epoch
        var emailData = "email body"u8.ToArray();
        var folderData = "folder info"u8.ToArray();

        var emailCt = standalone0.Encrypt(emailData, BlockType.EmailContent, blockId: 50);
        var folderCt = standalone0.Encrypt(folderData, BlockType.Folder, blockId: 51);

        // Decrypt both with epoch 0
        Assert.Equal(emailData, provider.Decrypt(emailCt, BlockType.EmailContent, blockId: 50, keyEpoch: 0));
        Assert.Equal(folderData, provider.Decrypt(folderCt, BlockType.Folder, blockId: 51, keyEpoch: 0));
    }
}
