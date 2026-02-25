using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "Updates EncryptionHeader with new salt and key verification token"
///
/// After a password change, the EncryptionHeader must be updated so that:
///   - Salt contains the new salt (used with the new password to derive the new KEK)
///   - KeyVerificationToken is re-encrypted with the new KEK
///   - The updated header can be written to and read back from a stream
///   - The old salt / old KEK can no longer verify the new token
/// </summary>
public class ChangePasswordUpdatesEncryptionHeaderTests
{
    private const int KeySize = 32;
    private const string KnownPlaintext = "EMDB";
    private const string OldPassword = "old-correct-horse-battery";
    private const string NewPassword = "new-correct-horse-battery";

    private readonly DefaultBlockContentSerializer _serializer = new();

    private static KeyStoreContent CreateSampleContent()
    {
        var dek = new byte[KeySize];
        RandomNumberGenerator.Fill(dek);

        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new()
                {
                    Epoch = 0,
                    DEK = dek,
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Retired = false
                }
            }
        };
    }

    /// <summary>
    /// Sets up an initial encrypted database state: EncryptionHeader + encrypted key store,
    /// both protected with the old password.
    /// </summary>
    private (EncryptionHeader header, byte[] encryptedKeyStore, KeyStoreContent original) SetupInitialState()
    {
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        // Create key verification token with the old KEK
        using var oldProvider = new AesGcmBlockEncryptionProvider(oldKek);
        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var oldToken = oldProvider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        var header = new EncryptionHeader
        {
            Salt = oldSalt,
            KeyVerificationToken = oldToken
        };

        var content = CreateSampleContent();
        var ksManager = new KeyStoreManager(_serializer);
        var encryptedKeyStore = ksManager.EncryptKeyStore(content, oldKek);

        return (header, encryptedKeyStore, content);
    }

    /// <summary>
    /// Simulates the full password-change flow including the EncryptionHeader update.
    /// Returns the old header, the updated header, and the new KEK.
    /// </summary>
    private (EncryptionHeader oldHeader, EncryptionHeader newHeader, byte[] newKek) PerformPasswordChange()
    {
        var (oldHeader, encryptedKeyStore, _) = SetupInitialState();

        // Step 1: Derive old KEK from old password + existing salt
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldHeader.Salt);

        // Step 2: Decrypt key store with old KEK
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        // Step 3: Generate new salt and derive new KEK
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        // Step 4: Re-encrypt key store with new KEK (covered by sibling tests)
        ksManager.EncryptKeyStore(decrypted, newKek);

        // Step 5: Update EncryptionHeader with new salt and new key verification token
        using var newProvider = new AesGcmBlockEncryptionProvider(newKek);
        var plainBytes = Encoding.ASCII.GetBytes(KnownPlaintext);
        var newToken = newProvider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        var newHeader = new EncryptionHeader
        {
            Magic = EncryptionHeader.MagicBytes,
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = newSalt,
            KeyVerificationToken = newToken
        };

        return (oldHeader, newHeader, newKek);
    }

    [Fact]
    public void UpdatedHeader_HasNewSalt_DifferentFromOldSalt()
    {
        var (oldHeader, newHeader, _) = PerformPasswordChange();

        Assert.NotEqual(oldHeader.Salt, newHeader.Salt);
    }

    [Fact]
    public void UpdatedHeader_NewSalt_IsCorrectLength()
    {
        var (_, newHeader, _) = PerformPasswordChange();

        Assert.Equal(KeyDerivation.SaltSize, newHeader.Salt.Length);
    }

    [Fact]
    public void UpdatedHeader_HasNewKeyVerificationToken()
    {
        var (oldHeader, newHeader, _) = PerformPasswordChange();

        Assert.NotEqual(oldHeader.KeyVerificationToken, newHeader.KeyVerificationToken);
    }

    [Fact]
    public void UpdatedHeader_NewToken_IsNotEmpty()
    {
        var (_, newHeader, _) = PerformPasswordChange();

        Assert.True(newHeader.KeyVerificationToken.Length > 0);
    }

    [Fact]
    public void UpdatedHeader_NewToken_DecryptsToEMDB_WithNewKek()
    {
        var (_, newHeader, newKek) = PerformPasswordChange();

        using var provider = new AesGcmBlockEncryptionProvider(newKek);
        var decrypted = provider.Decrypt(newHeader.KeyVerificationToken, BlockType.Metadata, blockId: 0);

        Assert.Equal(EncryptionHeader.MagicBytes, decrypted);
    }

    [Fact]
    public void UpdatedHeader_NewToken_DecryptsToEMDB_WithReDerivedNewKek()
    {
        // Simulate reopening the file: re-derive new KEK from new password + new salt in header
        var (_, newHeader, _) = PerformPasswordChange();

        var reDerivedKek = KeyDerivation.DeriveFromPassword(NewPassword, newHeader.Salt);
        using var provider = new AesGcmBlockEncryptionProvider(reDerivedKek);

        var decrypted = provider.Decrypt(newHeader.KeyVerificationToken, BlockType.Metadata, blockId: 0);
        Assert.Equal(KnownPlaintext, Encoding.ASCII.GetString(decrypted));
    }

    [Fact]
    public void UpdatedHeader_OldKek_CannotDecryptNewToken()
    {
        var (oldHeader, newHeader, _) = PerformPasswordChange();

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldHeader.Salt);
        using var provider = new AesGcmBlockEncryptionProvider(oldKek);

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(newHeader.KeyVerificationToken, BlockType.Metadata, blockId: 0));
    }

    [Fact]
    public void UpdatedHeader_OldPassword_WithNewSalt_CannotDecryptNewToken()
    {
        // Using the old password with the new salt still produces a different key
        var (_, newHeader, _) = PerformPasswordChange();

        var wrongKek = KeyDerivation.DeriveFromPassword(OldPassword, newHeader.Salt);
        using var provider = new AesGcmBlockEncryptionProvider(wrongKek);

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(newHeader.KeyVerificationToken, BlockType.Metadata, blockId: 0));
    }

    [Fact]
    public void UpdatedHeader_PreservesSchemeVersion()
    {
        var (oldHeader, newHeader, _) = PerformPasswordChange();

        Assert.Equal(EncryptionHeader.CurrentSchemeVersion, newHeader.SchemeVersion);
    }

    [Fact]
    public void UpdatedHeader_PreservesAlgorithmId()
    {
        var (_, newHeader, _) = PerformPasswordChange();

        Assert.Equal(EncryptionHeader.AlgorithmAes256Gcm, newHeader.AlgorithmId);
    }

    [Fact]
    public void UpdatedHeader_PreservesKdfType()
    {
        var (_, newHeader, _) = PerformPasswordChange();

        Assert.Equal(EncryptionHeader.KdfArgon2Id, newHeader.KdfType);
    }

    [Fact]
    public void UpdatedHeader_WriteAndReadRoundTrip_PreservesSalt()
    {
        var (_, newHeader, _) = PerformPasswordChange();

        using var ms = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(ms, newHeader);

        ms.Position = 0;
        var readResult = EncryptionHeaderManager.ReadHeader(ms);

        Assert.True(readResult.IsSuccess);
        Assert.Equal(newHeader.Salt, readResult.Value.Salt);
    }

    [Fact]
    public void UpdatedHeader_WriteAndReadRoundTrip_PreservesToken()
    {
        var (_, newHeader, _) = PerformPasswordChange();

        using var ms = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(ms, newHeader);

        ms.Position = 0;
        var readResult = EncryptionHeaderManager.ReadHeader(ms);

        Assert.True(readResult.IsSuccess);
        Assert.Equal(newHeader.KeyVerificationToken, readResult.Value.KeyVerificationToken);
    }

    [Fact]
    public void UpdatedHeader_WriteAndReadRoundTrip_TokenStillDecryptsToEMDB()
    {
        var (_, newHeader, newKek) = PerformPasswordChange();

        // Write the updated header to a stream and read it back
        using var ms = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(ms, newHeader);

        ms.Position = 0;
        var readResult = EncryptionHeaderManager.ReadHeader(ms);

        Assert.True(readResult.IsSuccess);

        // Re-derive KEK from the round-tripped header's salt and verify the token
        var reDerivedKek = KeyDerivation.DeriveFromPassword(NewPassword, readResult.Value.Salt);
        using var provider = new AesGcmBlockEncryptionProvider(reDerivedKek);

        var decrypted = provider.Decrypt(readResult.Value.KeyVerificationToken, BlockType.Metadata, blockId: 0);
        Assert.Equal(EncryptionHeader.MagicBytes, decrypted);
    }
}
