using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that AesGcmBlockEncryptionProvider decrypts correctly
/// and that AES-GCM auth tag verification rejects tampered data.
/// </summary>
public class AesGcmDecryptVerifiesAuthTagTests
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[] GenerateKey()
    {
        var key = new byte[AesGcmBlockEncryptionProvider.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Decrypt_RecoversOriginalPlaintext()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
        long blockId = 42;

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_EmptyPlaintext_RecoversEmpty()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = Array.Empty<byte>();

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 0);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 0);

        Assert.Empty(decrypted);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Tamper with a byte in the ciphertext region (between nonce and tag)
        int ciphertextIndex = NonceSize + 2;
        encrypted[ciphertextIndex] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Decrypt_TamperedAuthTag_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 5);

        // Tamper with the last byte of the auth tag
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 5));
    }

    [Fact]
    public void Decrypt_TamperedNonce_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 10);

        // Tamper with a byte in the nonce region
        encrypted[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 10));
    }

    [Fact]
    public void Decrypt_TooShortInput_ThrowsCryptographicException()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);

        // Input shorter than NonceSize + TagSize = 28 bytes
        var tooShort = new byte[NonceSize + TagSize - 1];

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(tooShort, BlockType.EmailContent, blockId: 1));
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        var key1 = GenerateKey();
        var key2 = GenerateKey();
        using var encryptor = new AesGcmBlockEncryptionProvider(key1);
        using var decryptor = new AesGcmBlockEncryptionProvider(key2);
        var plaintext = new byte[] { 0x01, 0x02, 0x03 };

        var encrypted = encryptor.Encrypt(plaintext, BlockType.EmailContent, blockId: 7);

        Assert.ThrowsAny<CryptographicException>(() =>
            decryptor.Decrypt(encrypted, BlockType.EmailContent, blockId: 7));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(4096)]
    public void Decrypt_VariousPayloadSizes_RecoversPlaintext(int size)
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[size];
        RandomNumberGenerator.Fill(plaintext);

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 99);
        var decrypted = provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 99);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_AuthTagVerification_FailsWhenSingleBitFlipped()
    {
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Flip a single bit in the auth tag (first byte of tag)
        int tagStart = encrypted.Length - TagSize;
        encrypted[tagStart] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 1));
    }
}
