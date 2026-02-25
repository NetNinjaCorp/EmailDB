using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-42:
/// "Wrong key = immediate error"
///
/// When a user opens an encrypted database file with the wrong password,
/// the system must detect it immediately via the EncryptionHeader's key
/// verification token and throw a CryptographicException — before
/// attempting to read any blocks.
/// </summary>
public class WrongKeyImmediateErrorTests
{
    private const string KnownPlaintext = "EMDB";
    private const string CorrectPassword = "correct-password-42";
    private const string WrongPassword = "wrong-password-99";

    /// <summary>
    /// Builds a fully populated EncryptionHeader as it would appear on disk.
    /// </summary>
    private static (EncryptionHeader Header, byte[] Key) BuildEncryptedHeader(string password)
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(password, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var token = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        var header = new EncryptionHeader
        {
            Magic = EncryptionHeader.MagicBytes,
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = salt,
            KeyVerificationToken = token
        };

        return (header, key);
    }

    /// <summary>
    /// Simulates the key verification step that runs when opening a database file.
    /// Returns the decrypted plaintext if the key is correct; throws otherwise.
    /// </summary>
    private static byte[] VerifyKey(EncryptionHeader header, byte[] key)
    {
        using var provider = new AesGcmBlockEncryptionProvider(key);
        return provider.Decrypt(header.KeyVerificationToken, BlockType.Metadata, blockId: 0);
    }

    [Fact]
    public void WrongPassword_ThrowsCryptographicException_Immediately()
    {
        var (header, _) = BuildEncryptedHeader(CorrectPassword);

        // Derive key from wrong password using the header's stored salt
        var wrongKey = KeyDerivation.DeriveFromPassword(WrongPassword, header.Salt);

        Assert.ThrowsAny<CryptographicException>(() => VerifyKey(header, wrongKey));
    }

    [Fact]
    public void CorrectPassword_DecryptsToKnownPlaintext()
    {
        var (header, correctKey) = BuildEncryptedHeader(CorrectPassword);

        var decrypted = VerifyKey(header, correctKey);
        Assert.Equal(KnownPlaintext, Encoding.ASCII.GetString(decrypted));
    }

    [Fact]
    public void WrongPassword_ThrowsAuthenticationTagMismatchException()
    {
        var (header, _) = BuildEncryptedHeader(CorrectPassword);
        var wrongKey = KeyDerivation.DeriveFromPassword(WrongPassword, header.Salt);

        Assert.Throws<AuthenticationTagMismatchException>(() => VerifyKey(header, wrongKey));
    }

    [Fact]
    public void WrongPassword_ErrorOccursBeforeAnyBlockRead()
    {
        // The key verification check uses only the header's token — no block
        // reads are needed. This test confirms the error is thrown from the
        // header verification step alone (blockId: 0, Metadata type).
        var (header, _) = BuildEncryptedHeader(CorrectPassword);
        var wrongKey = KeyDerivation.DeriveFromPassword(WrongPassword, header.Salt);

        var ex = Assert.ThrowsAny<CryptographicException>(() => VerifyKey(header, wrongKey));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message),
            "Error must include a descriptive message for the caller.");
    }

    [Fact]
    public void WrongPassword_ReDerivedFromSameSalt_StillFails()
    {
        // Even when using the same salt stored in the header, a wrong password
        // must always fail key verification.
        var (header, _) = BuildEncryptedHeader(CorrectPassword);

        var key1 = KeyDerivation.DeriveFromPassword(WrongPassword, header.Salt);
        var key2 = KeyDerivation.DeriveFromPassword(WrongPassword, header.Salt);

        // Both derivations with the same wrong password produce the same key
        Assert.Equal(key1, key2);

        // And both fail verification
        Assert.ThrowsAny<CryptographicException>(() => VerifyKey(header, key1));
        Assert.ThrowsAny<CryptographicException>(() => VerifyKey(header, key2));
    }

    [Fact]
    public void WrongPassword_NeverReturnsSilentGarbage()
    {
        var (header, _) = BuildEncryptedHeader(CorrectPassword);
        var wrongKey = KeyDerivation.DeriveFromPassword(WrongPassword, header.Salt);

        bool threw = false;
        try
        {
            VerifyKey(header, wrongKey);
        }
        catch (CryptographicException)
        {
            threw = true;
        }

        Assert.True(threw,
            "Wrong key must throw CryptographicException, not silently return incorrect data.");
    }

    [Fact]
    public void WrongKeyFile_ThrowsCryptographicException()
    {
        // Simulate providing a wrong raw key (not password-derived)
        var (header, _) = BuildEncryptedHeader(CorrectPassword);

        var wrongKey = new byte[KeyDerivation.KeySize];
        RandomNumberGenerator.Fill(wrongKey);

        Assert.ThrowsAny<CryptographicException>(() => VerifyKey(header, wrongKey));
    }

    [Fact]
    public void SingleBitFlipInKey_ThrowsCryptographicException()
    {
        var (header, correctKey) = BuildEncryptedHeader(CorrectPassword);

        var flippedKey = (byte[])correctKey.Clone();
        flippedKey[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => VerifyKey(header, flippedKey));
    }
}
