using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class ExistingBlocksReadableWithOriginalDekTests
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
    public void PreRotationBlock_DecryptableWithOriginalEpoch()
    {
        var keyStore = CreateSingleEpochKeyStore();

        // Encrypt a block before rotation
        using var preProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Existing block data"u8.ToArray();
        var ciphertext = preProvider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Rotate the key
        _manager.RotateKey(keyStore);

        // Create provider with rotated keystore (has both epoch 0 and epoch 1)
        using var postProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Decrypt with original epoch 0
        var decrypted = postProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void PreRotationBlock_StillReadableAfterMultipleRotations()
    {
        var keyStore = CreateSingleEpochKeyStore();

        // Encrypt before any rotation
        using var preProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Very old block"u8.ToArray();
        var ciphertext = preProvider.Encrypt(plaintext, BlockType.EmailContent, blockId: 10);

        // Rotate multiple times
        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);

        // Epoch 0's DEK is still in the keystore
        using var postProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var decrypted = postProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 10, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void BlocksFromEachEpoch_AllReadableWithTheirOriginalDek()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var ciphertexts = new List<(byte[] Ciphertext, int Epoch, byte[] Plaintext)>();

        // Encrypt a block at epoch 0
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            var plaintext = "Epoch 0 block"u8.ToArray();
            var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
            ciphertexts.Add((ciphertext, 0, plaintext));
        }

        // Rotate and encrypt at epoch 1
        _manager.RotateKey(keyStore);
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            var plaintext = "Epoch 1 block"u8.ToArray();
            var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 2);
            ciphertexts.Add((ciphertext, 1, plaintext));
        }

        // Rotate and encrypt at epoch 2
        _manager.RotateKey(keyStore);
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            var plaintext = "Epoch 2 block"u8.ToArray();
            var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 3);
            ciphertexts.Add((ciphertext, 2, plaintext));
        }

        // All blocks should be readable with their original epoch's DEK
        using var finalProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        foreach (var (ciphertext, epoch, expectedPlaintext) in ciphertexts)
        {
            var decrypted = finalProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: epoch + 1, keyEpoch: epoch);
            Assert.Equal(expectedPlaintext, decrypted);
        }
    }

    [Fact]
    public void PreRotationBlock_OriginalDekBytesUnchangedAfterRotation()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var originalDek = keyStore.Entries[0].DEK.ToArray();

        // Encrypt before rotation
        using var preProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Check DEK preservation"u8.ToArray();
        var ciphertext = preProvider.Encrypt(plaintext, BlockType.EmailContent, blockId: 5);

        // Rotate
        _manager.RotateKey(keyStore);

        // Verify the original DEK bytes are unchanged in the keystore
        var epoch0Entry = keyStore.Entries.First(e => e.Epoch == 0);
        Assert.Equal(originalDek, epoch0Entry.DEK);

        // And the block is still decryptable with a standalone provider using that DEK
        using var standalone = new AesGcmBlockEncryptionProvider(epoch0Entry.DEK);
        var decrypted = standalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 5);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void PreRotationBlock_DifferentBlockTypes_AllReadable()
    {
        var keyStore = CreateSingleEpochKeyStore();

        var blockTypes = new[] { BlockType.EmailContent, BlockType.Folder, BlockType.FolderTree };
        var ciphertexts = new Dictionary<BlockType, (byte[] Ciphertext, byte[] Plaintext)>();

        // Encrypt blocks of various types before rotation
        using (var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default))
        {
            long blockId = 1;
            foreach (var blockType in blockTypes)
            {
                var plaintext = System.Text.Encoding.UTF8.GetBytes($"Pre-rotation {blockType}");
                var ciphertext = provider.Encrypt(plaintext, blockType, blockId);
                ciphertexts[blockType] = (ciphertext, plaintext);
                blockId++;
            }
        }

        // Rotate
        _manager.RotateKey(keyStore);

        // All pre-rotation blocks should be readable with original epoch
        using var postProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        long id = 1;
        foreach (var blockType in blockTypes)
        {
            var (ciphertext, expectedPlaintext) = ciphertexts[blockType];
            var decrypted = postProvider.Decrypt(ciphertext, blockType, id, keyEpoch: 0);
            Assert.Equal(expectedPlaintext, decrypted);
            id++;
        }
    }

    [Fact]
    public void PreRotationBlock_DefaultDecryptFailsButEpochSpecificSucceeds()
    {
        var keyStore = CreateSingleEpochKeyStore();

        // Encrypt at epoch 0
        using var preProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Epoch 0 data"u8.ToArray();
        var ciphertext = preProvider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Rotate to epoch 1
        _manager.RotateKey(keyStore);

        using var postProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Default Decrypt (uses active epoch 1) should fail for epoch 0 ciphertext
        Assert.ThrowsAny<CryptographicException>(() =>
            postProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1));

        // Explicit epoch 0 Decrypt should succeed
        var decrypted = postProvider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 0);
        Assert.Equal(plaintext, decrypted);
    }
}
