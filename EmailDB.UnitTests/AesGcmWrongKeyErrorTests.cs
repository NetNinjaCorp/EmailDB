using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that decrypting with the wrong key fails with a clear,
/// specific CryptographicException rather than a generic or silent error.
/// Acceptance criterion: "Wrong key fails with clear error" (US-EMDB-39).
/// </summary>
public class AesGcmWrongKeyErrorTests
{
    private static byte[] GenerateKey()
    {
        var key = new byte[AesGcmBlockEncryptionProvider.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        var correctKey = GenerateKey();
        var wrongKey = GenerateKey();
        using var encryptor = new AesGcmBlockEncryptionProvider(correctKey);
        using var decryptor = new AesGcmBlockEncryptionProvider(wrongKey);
        var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"

        var encrypted = encryptor.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        var ex = Assert.ThrowsAny<CryptographicException>(() =>
            decryptor.Decrypt(encrypted, BlockType.EmailContent, blockId: 1));

        // The exception must have a non-empty message so callers get a clear error
        Assert.False(string.IsNullOrWhiteSpace(ex.Message),
            "CryptographicException should contain a descriptive error message.");
    }

    [Fact]
    public void Decrypt_WrongKey_ThrowsAuthenticationTagMismatchException()
    {
        // .NET 9+ throws AuthenticationTagMismatchException (subclass of
        // CryptographicException), giving callers a very specific error type
        // that clearly indicates the key was wrong or data was tampered.
        var correctKey = GenerateKey();
        var wrongKey = GenerateKey();
        using var encryptor = new AesGcmBlockEncryptionProvider(correctKey);
        using var decryptor = new AesGcmBlockEncryptionProvider(wrongKey);
        var plaintext = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var encrypted = encryptor.Encrypt(plaintext, BlockType.EmailContent, blockId: 2);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            decryptor.Decrypt(encrypted, BlockType.EmailContent, blockId: 2));
    }

    [Fact]
    public void Decrypt_WrongKey_SingleByteDifference_ThrowsCryptographicException()
    {
        // Even a single-byte key difference must fail authentication
        var key = GenerateKey();
        var alteredKey = (byte[])key.Clone();
        alteredKey[0] ^= 0x01; // flip one bit

        using var encryptor = new AesGcmBlockEncryptionProvider(key);
        using var decryptor = new AesGcmBlockEncryptionProvider(alteredKey);
        var plaintext = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        var encrypted = encryptor.Encrypt(plaintext, BlockType.EmailContent, blockId: 3);

        Assert.ThrowsAny<CryptographicException>(() =>
            decryptor.Decrypt(encrypted, BlockType.EmailContent, blockId: 3));
    }

    [Fact]
    public void Decrypt_WrongKey_DoesNotReturnGarbage()
    {
        // Ensure wrong key throws rather than silently returning incorrect data
        var correctKey = GenerateKey();
        var wrongKey = GenerateKey();
        using var encryptor = new AesGcmBlockEncryptionProvider(correctKey);
        using var decryptor = new AesGcmBlockEncryptionProvider(wrongKey);
        var plaintext = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        var encrypted = encryptor.Encrypt(plaintext, BlockType.EmailContent, blockId: 4);

        bool threw = false;
        try
        {
            decryptor.Decrypt(encrypted, BlockType.EmailContent, blockId: 4);
        }
        catch (CryptographicException)
        {
            threw = true;
        }

        Assert.True(threw,
            "Decrypt with wrong key must throw CryptographicException, not silently return wrong data.");
    }

    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.BTreeLeaf)]
    [InlineData(BlockType.IndexRoot)]
    public void Decrypt_WrongKey_AllBlockTypes_ThrowsCryptographicException(BlockType blockType)
    {
        var correctKey = GenerateKey();
        var wrongKey = GenerateKey();
        using var encryptor = new AesGcmBlockEncryptionProvider(correctKey);
        using var decryptor = new AesGcmBlockEncryptionProvider(wrongKey);
        var plaintext = new byte[] { 0x01, 0x02, 0x03 };

        var encrypted = encryptor.Encrypt(plaintext, blockType, blockId: 5);

        Assert.ThrowsAny<CryptographicException>(() =>
            decryptor.Decrypt(encrypted, blockType, blockId: 5));
    }
}
