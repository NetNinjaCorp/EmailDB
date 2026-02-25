using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-42:
/// "Key verification token = encrypt known plaintext EMDB with derived key"
/// </summary>
public class KeyVerificationTokenTests
{
    private const string KnownPlaintext = "EMDB";
    private const string TestPassword = "test-password-42";

    [Fact]
    public void KeyVerificationToken_IsEncryptedKnownPlaintext()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        var header = new EncryptionHeader
        {
            Salt = salt,
            KeyVerificationToken = token
        };

        // Decrypt the token and verify it matches the known plaintext
        var decrypted = provider.Decrypt(header.KeyVerificationToken, BlockType.Metadata, blockId: 0);
        Assert.Equal(KnownPlaintext, Encoding.ASCII.GetString(decrypted));
    }

    [Fact]
    public void KeyVerificationToken_DecryptsToEMDB()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        // The decrypted result must be exactly the 4-byte ASCII "EMDB"
        var decrypted = provider.Decrypt(token, BlockType.Metadata, blockId: 0);
        Assert.Equal(EncryptionHeader.MagicBytes, decrypted);
    }

    [Fact]
    public void KeyVerificationToken_WrongKey_FailsDecryption()
    {
        var salt = KeyDerivation.GenerateSalt();
        var correctKey = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        var wrongKey = KeyDerivation.DeriveFromPassword("wrong-password", salt);

        using var encProvider = new AesGcmBlockEncryptionProvider(correctKey);
        using var decProvider = new AesGcmBlockEncryptionProvider(wrongKey);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token = encProvider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        Assert.ThrowsAny<CryptographicException>(() =>
            decProvider.Decrypt(token, BlockType.Metadata, blockId: 0));
    }

    [Fact]
    public void KeyVerificationToken_SameKey_DifferentInvocations_BothDecrypt()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token1 = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);
        var token2 = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        // Tokens differ due to random nonce component
        Assert.NotEqual(token1, token2);

        // But both decrypt to the same known plaintext
        Assert.Equal(KnownPlaintext, Encoding.ASCII.GetString(provider.Decrypt(token1, BlockType.Metadata, blockId: 0)));
        Assert.Equal(KnownPlaintext, Encoding.ASCII.GetString(provider.Decrypt(token2, BlockType.Metadata, blockId: 0)));
    }

    [Fact]
    public void KeyVerificationToken_ReDerivedKey_DecryptsToken()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key1 = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider1 = new AesGcmBlockEncryptionProvider(key1);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token = provider1.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        // Simulate reopening the file: re-derive key from same password + salt
        var key2 = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider2 = new AesGcmBlockEncryptionProvider(key2);

        var decrypted = provider2.Decrypt(token, BlockType.Metadata, blockId: 0);
        Assert.Equal(KnownPlaintext, Encoding.ASCII.GetString(decrypted));
    }

    [Fact]
    public void KeyVerificationToken_StoredInHeader_Roundtrips()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        // Build header as it would be written to disk
        var header = new EncryptionHeader
        {
            Magic = EncryptionHeader.MagicBytes,
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = salt,
            KeyVerificationToken = token
        };

        // Simulate reading the header back and verifying the key
        var reDerivedKey = KeyDerivation.DeriveFromPassword(TestPassword, header.Salt);
        using var verifier = new AesGcmBlockEncryptionProvider(reDerivedKey);

        var decrypted = verifier.Decrypt(header.KeyVerificationToken, BlockType.Metadata, blockId: 0);
        Assert.Equal(EncryptionHeader.MagicBytes, decrypted);
    }
}
