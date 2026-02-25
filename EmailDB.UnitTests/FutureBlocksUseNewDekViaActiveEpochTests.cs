using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class FutureBlocksUseNewDekViaActiveEpochTests
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
    public void AfterRotation_ProviderActiveEpochMatchesNewEpoch()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        Assert.Equal(1, provider.ActiveEpoch);
    }

    [Fact]
    public void AfterRotation_EncryptUsesNewDek()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var originalDek = keyStore.Entries[0].DEK.ToArray();

        _manager.RotateKey(keyStore);
        var newDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK;

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Future block data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 100);

        // New DEK should decrypt the ciphertext
        using var newStandalone = new AesGcmBlockEncryptionProvider(newDek);
        var decrypted = newStandalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 100);
        Assert.Equal(plaintext, decrypted);

        // Original DEK should NOT decrypt the ciphertext
        using var oldStandalone = new AesGcmBlockEncryptionProvider(originalDek);
        Assert.ThrowsAny<CryptographicException>(() =>
            oldStandalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 100));
    }

    [Fact]
    public void AfterMultipleRotations_EncryptUsesLatestDek()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);

        var latestDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK;

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Third rotation block"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 200);

        using var standalone = new AesGcmBlockEncryptionProvider(latestDek);
        var decrypted = standalone.Decrypt(ciphertext, BlockType.EmailContent, blockId: 200);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AfterRotation_MultipleBlocksAllUseNewDek()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var newDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK;

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        for (long blockId = 1; blockId <= 10; blockId++)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes($"Future block {blockId}");
            var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

            using var standalone = new AesGcmBlockEncryptionProvider(newDek);
            var decrypted = standalone.Decrypt(ciphertext, BlockType.EmailContent, blockId);
            Assert.Equal(plaintext, decrypted);
        }
    }

    [Fact]
    public void AfterRotation_EncryptDecryptRoundTripsViaProvider()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Round trip after rotation"u8.ToArray();

        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 50);
        var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 50);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AfterRotation_DifferentBlockTypesUseNewDek()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var newDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK;

        using var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Full);

        var blockTypes = new[] { BlockType.EmailContent, BlockType.Folder, BlockType.Metadata };
        foreach (var blockType in blockTypes)
        {
            if (!provider.ShouldEncrypt(blockType)) continue;

            var plaintext = System.Text.Encoding.UTF8.GetBytes($"Data for {blockType}");
            var ciphertext = provider.Encrypt(plaintext, blockType, blockId: 300);

            using var standalone = new AesGcmBlockEncryptionProvider(newDek);
            var decrypted = standalone.Decrypt(ciphertext, blockType, blockId: 300);
            Assert.Equal(plaintext, decrypted);
        }
    }

    [Fact]
    public void PreRotationBlocks_NotDecryptableWithNewDek()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var originalDek = keyStore.Entries[0].DEK.ToArray();

        // Encrypt a block before rotation
        using var preProvider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);
        var plaintext = "Pre-rotation block"u8.ToArray();
        var preRotationCiphertext = preProvider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        _manager.RotateKey(keyStore);
        var newDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK;

        // New DEK should NOT decrypt the pre-rotation ciphertext
        using var newStandalone = new AesGcmBlockEncryptionProvider(newDek);
        Assert.ThrowsAny<CryptographicException>(() =>
            newStandalone.Decrypt(preRotationCiphertext, BlockType.EmailContent, blockId: 1));

        // Original DEK should still decrypt it
        using var oldStandalone = new AesGcmBlockEncryptionProvider(originalDek);
        var decrypted = oldStandalone.Decrypt(preRotationCiphertext, BlockType.EmailContent, blockId: 1);
        Assert.Equal(plaintext, decrypted);
    }
}
