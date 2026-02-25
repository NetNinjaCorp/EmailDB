using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class OldNewEpochBlocksCoexistDecryptableTests
{
    private readonly KeyStoreManager _manager = new(new DefaultBlockContentSerializer());

    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private KeyStoreContent CreateSingleEpochKeyStore()
    {
        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = GenerateDek(), Timestamp = DateTime.UtcNow, Retired = false }
            }
        };
    }

    [Fact]
    public void AfterRotation_BothOldAndNewEpochBlocksDecryptWithSingleProvider()
    {
        var keyStore = CreateSingleEpochKeyStore();

        // Encrypt a block at epoch 0
        byte[] oldPlaintext = "Old epoch 0 block data"u8.ToArray();
        byte[] oldCiphertext;
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            oldCiphertext = provider.Encrypt(oldPlaintext, BlockType.EmailContent, blockId: 1);
        }

        // Rotate to epoch 1
        _manager.RotateKey(keyStore);

        // Encrypt a block at epoch 1
        byte[] newPlaintext = "New epoch 1 block data"u8.ToArray();
        byte[] newCiphertext;
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            newCiphertext = provider.Encrypt(newPlaintext, BlockType.EmailContent, blockId: 2);
        }

        // A single provider with the full keystore can decrypt both
        using var finalProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var decryptedOld = finalProvider.Decrypt(oldCiphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 0);
        var decryptedNew = finalProvider.Decrypt(newCiphertext, BlockType.EmailContent, blockId: 2, keyEpoch: 1);

        Assert.Equal(oldPlaintext, decryptedOld);
        Assert.Equal(newPlaintext, decryptedNew);
    }

    [Fact]
    public void MultipleRotations_AllEpochBlocksCoexistAndDecrypt()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var blocks = new List<(byte[] Ciphertext, byte[] Plaintext, int Epoch, long BlockId)>();

        // Write blocks across 4 epochs (0, 1, 2, 3)
        for (int epoch = 0; epoch < 4; epoch++)
        {
            using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
            var plaintext = System.Text.Encoding.UTF8.GetBytes($"Block at epoch {epoch}");
            long blockId = epoch + 1;
            var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: blockId);
            blocks.Add((ciphertext, plaintext, epoch, blockId));

            if (epoch < 3)
                _manager.RotateKey(keyStore);
        }

        // All 4 epoch blocks coexist — verify they are all decryptable
        using var finalProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        foreach (var (ciphertext, expectedPlaintext, epoch, blockId) in blocks)
        {
            var decrypted = finalProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: blockId, keyEpoch: epoch);
            Assert.Equal(expectedPlaintext, decrypted);
        }
    }

    [Fact]
    public void CoexistingBlocks_CrossEpochDecryptionWithWrongEpochFails()
    {
        var keyStore = CreateSingleEpochKeyStore();

        // Encrypt at epoch 0
        byte[] plaintext0 = "Epoch 0 content"u8.ToArray();
        byte[] ciphertext0;
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            ciphertext0 = provider.Encrypt(plaintext0, BlockType.EmailContent, blockId: 1);
        }

        // Rotate and encrypt at epoch 1
        _manager.RotateKey(keyStore);
        byte[] plaintext1 = "Epoch 1 content"u8.ToArray();
        byte[] ciphertext1;
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            ciphertext1 = provider.Encrypt(plaintext1, BlockType.EmailContent, blockId: 2);
        }

        using var finalProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Correct epochs succeed
        Assert.Equal(plaintext0, finalProvider.Decrypt(ciphertext0, BlockType.EmailContent, blockId: 1, keyEpoch: 0));
        Assert.Equal(plaintext1, finalProvider.Decrypt(ciphertext1, BlockType.EmailContent, blockId: 2, keyEpoch: 1));

        // Wrong epochs fail — epoch 0 ciphertext with epoch 1 key
        Assert.ThrowsAny<CryptographicException>(() =>
            finalProvider.Decrypt(ciphertext0, BlockType.EmailContent, blockId: 1, keyEpoch: 1));

        // Wrong epochs fail — epoch 1 ciphertext with epoch 0 key
        Assert.ThrowsAny<CryptographicException>(() =>
            finalProvider.Decrypt(ciphertext1, BlockType.EmailContent, blockId: 2, keyEpoch: 0));
    }

    [Fact]
    public void CoexistingBlocks_DifferentBlockTypesAcrossEpochs()
    {
        var keyStore = CreateSingleEpochKeyStore();

        // Epoch 0: EmailContent block
        byte[] emailPlaintext = "Email at epoch 0"u8.ToArray();
        byte[] emailCiphertext;
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            emailCiphertext = provider.Encrypt(emailPlaintext, BlockType.EmailContent, blockId: 10);
        }

        // Rotate to epoch 1
        _manager.RotateKey(keyStore);

        // Epoch 1: Folder block
        byte[] folderPlaintext = "Folder at epoch 1"u8.ToArray();
        byte[] folderCiphertext;
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            folderCiphertext = provider.Encrypt(folderPlaintext, BlockType.Folder, blockId: 20);
        }

        // Both block types from different epochs are decryptable
        using var finalProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var decryptedEmail = finalProvider.Decrypt(emailCiphertext, BlockType.EmailContent, blockId: 10, keyEpoch: 0);
        var decryptedFolder = finalProvider.Decrypt(folderCiphertext, BlockType.Folder, blockId: 20, keyEpoch: 1);

        Assert.Equal(emailPlaintext, decryptedEmail);
        Assert.Equal(folderPlaintext, decryptedFolder);
    }

    [Fact]
    public void CoexistingBlocks_MultipleBlocksPerEpochAllDecryptable()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var blocks = new List<(byte[] Ciphertext, byte[] Plaintext, int Epoch, long BlockId)>();

        // Write 3 blocks at epoch 0
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            for (long id = 1; id <= 3; id++)
            {
                var plaintext = System.Text.Encoding.UTF8.GetBytes($"Epoch 0, block {id}");
                var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: id);
                blocks.Add((ciphertext, plaintext, 0, id));
            }
        }

        // Rotate and write 3 blocks at epoch 1
        _manager.RotateKey(keyStore);
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            for (long id = 4; id <= 6; id++)
            {
                var plaintext = System.Text.Encoding.UTF8.GetBytes($"Epoch 1, block {id}");
                var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: id);
                blocks.Add((ciphertext, plaintext, 1, id));
            }
        }

        // All 6 blocks across both epochs are decryptable
        using var finalProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        foreach (var (ciphertext, expectedPlaintext, epoch, blockId) in blocks)
        {
            var decrypted = finalProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: blockId, keyEpoch: epoch);
            Assert.Equal(expectedPlaintext, decrypted);
        }
    }
}
