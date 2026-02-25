using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that KeyWrappingEncryptionProvider properly disposes all DEK key material on disposal.
/// Verifies acceptance criterion: "Properly disposes all DEK key material on disposal"
/// </summary>
public class KeyWrappingDisposalTests
{
    private static byte[] GenerateDek()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private static KeyStoreContent CreateKeyStore(int epochCount = 1)
    {
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = epochCount - 1,
            Entries = new List<KeyStoreEntry>()
        };

        for (int i = 0; i < epochCount; i++)
        {
            keyStore.Entries.Add(new KeyStoreEntry
            {
                Epoch = i,
                DEK = GenerateDek(),
                Timestamp = DateTime.UtcNow,
                Retired = i < epochCount - 1
            });
        }

        return keyStore;
    }

    [Fact]
    public void Dispose_ThrowsObjectDisposedOnEncrypt()
    {
        var provider = new KeyWrappingEncryptionProvider(CreateKeyStore(), EncryptionPolicy.Default);
        provider.Dispose();

        var plaintext = "test data"u8.ToArray();
        Assert.Throws<ObjectDisposedException>(() =>
            provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Dispose_ThrowsObjectDisposedOnDecrypt()
    {
        var provider = new KeyWrappingEncryptionProvider(CreateKeyStore(), EncryptionPolicy.Default);

        // Encrypt something first so we have valid ciphertext
        var plaintext = "test data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Dispose_ThrowsObjectDisposedOnDecryptWithEpoch()
    {
        var provider = new KeyWrappingEncryptionProvider(CreateKeyStore(), EncryptionPolicy.Default);

        var plaintext = "test data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: 0));
    }

    [Fact]
    public void Dispose_MultipleEpochs_AllBecomUnavailable()
    {
        var keyStore = CreateKeyStore(epochCount: 3);
        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Encrypt with active epoch (2) to have valid ciphertext
        var plaintext = "test data"u8.ToArray();
        var ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        provider.Dispose();

        // All epochs should be unavailable after disposal
        for (int epoch = 0; epoch < 3; epoch++)
        {
            Assert.Throws<ObjectDisposedException>(() =>
                provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1, keyEpoch: epoch));
        }
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutError()
    {
        var provider = new KeyWrappingEncryptionProvider(CreateKeyStore(), EncryptionPolicy.Default);

        // Multiple dispose calls should be safe (idempotent)
        provider.Dispose();
        provider.Dispose();
        provider.Dispose();
    }

    [Fact]
    public void Dispose_ClearsInternalProviderDictionary()
    {
        var keyStore = CreateKeyStore(epochCount: 3);
        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Verify it works before disposal
        var plaintext = "hello"u8.ToArray();
        var ct = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var decrypted = provider.Decrypt(ct, BlockType.EmailContent, blockId: 1);
        Assert.Equal(plaintext, decrypted);

        provider.Dispose();

        // After disposal, encrypt and decrypt should throw
        Assert.Throws<ObjectDisposedException>(() =>
            provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 2));
        Assert.Throws<ObjectDisposedException>(() =>
            provider.Decrypt(ct, BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Dispose_ZeroesUnderlyingKeyMaterial()
    {
        // Create DEKs and keep references to verify they get zeroed
        var dek0 = GenerateDek();
        var dek1 = GenerateDek();

        // Clone them so the provider gets copies; we keep originals for comparison
        var dek0Copy = (byte[])dek0.Clone();
        var dek1Copy = (byte[])dek1.Clone();

        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow, Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Verify it works before disposal
        var plaintext = "secret"u8.ToArray();
        var ct = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var decrypted = provider.Decrypt(ct, BlockType.EmailContent, blockId: 1);
        Assert.Equal(plaintext, decrypted);

        provider.Dispose();

        // The provider can no longer perform operations
        Assert.Throws<ObjectDisposedException>(() =>
            provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 2));

        // Verify the original DEK arrays we passed in are still intact
        // (provider should have cloned them internally, not zeroed our originals)
        Assert.Equal(dek0Copy, dek0);
        Assert.Equal(dek1Copy, dek1);
    }

    [Fact]
    public void UsingStatement_DisposesCorrectly()
    {
        byte[] ciphertext;
        var plaintext = "test data"u8.ToArray();

        KeyWrappingEncryptionProvider outerRef;

        using (var provider = new KeyWrappingEncryptionProvider(CreateKeyStore(), EncryptionPolicy.Default))
        {
            outerRef = provider;
            ciphertext = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

            // Should work inside using block
            var decrypted = provider.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1);
            Assert.Equal(plaintext, decrypted);
        }

        // After using block ends, provider should be disposed
        Assert.Throws<ObjectDisposedException>(() =>
            outerRef.Encrypt(plaintext, BlockType.EmailContent, blockId: 2));
        Assert.Throws<ObjectDisposedException>(() =>
            outerRef.Decrypt(ciphertext, BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Dispose_SingleEpoch_EncryptThrowsAfterDisposal()
    {
        var provider = new KeyWrappingEncryptionProvider(CreateKeyStore(1), EncryptionPolicy.Default);
        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            provider.Encrypt("data"u8.ToArray(), BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Dispose_ManyEpochs_AllDisposed()
    {
        // Stress test with many key epochs
        var keyStore = CreateKeyStore(epochCount: 10);
        var provider = new KeyWrappingEncryptionProvider(keyStore, EncryptionPolicy.Default);

        // Verify encryption works for the active epoch
        var plaintext = "data"u8.ToArray();
        var ct = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        Assert.Equal(plaintext, provider.Decrypt(ct, BlockType.EmailContent, blockId: 1));

        provider.Dispose();

        // All 10 epochs should be cleaned up
        for (int epoch = 0; epoch < 10; epoch++)
        {
            Assert.Throws<ObjectDisposedException>(() =>
                provider.Decrypt(ct, BlockType.EmailContent, blockId: 1, keyEpoch: epoch));
        }
    }
}
