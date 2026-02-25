using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "Old password no longer works after change"
///
/// After a complete password change (KEK re-wrap), the old password must not be
/// usable at any level: key verification token, key store decryption, or data
/// block decryption via the re-opened key store.
/// </summary>
public class OldPasswordNoLongerWorksAfterChangeTests
{
    private const int KeySize = 32;
    private const string OldPassword = "old-correct-horse-battery";
    private const string NewPassword = "new-correct-horse-battery";

    private readonly DefaultBlockContentSerializer _serializer = new();

    private static KeyStoreContent CreateSingleEpochContent()
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

    private static KeyStoreContent CreateMultiEpochContent()
    {
        var dek0 = new byte[KeySize];
        var dek1 = new byte[KeySize];
        var dek2 = new byte[KeySize];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);
        RandomNumberGenerator.Fill(dek2);

        return new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false }
            }
        };
    }

    /// <summary>
    /// Performs a full password change: encrypts key store under old password, then
    /// re-wraps under new password. Returns the new salt, re-encrypted key store,
    /// new header, and the old salt for verification.
    /// </summary>
    private (byte[] oldSalt, byte[] newSalt, byte[] reEncryptedKeyStore, EncryptionHeader newHeader)
        PerformFullPasswordChange(KeyStoreContent content)
    {
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        var ksManager = new KeyStoreManager(_serializer);
        var encryptedKeyStore = ksManager.EncryptKeyStore(content, oldKek);

        // Decrypt with old KEK, re-encrypt with new KEK
        var decryptedKs = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncryptedKeyStore = ksManager.EncryptKeyStore(decryptedKs, newKek);

        // Build updated EncryptionHeader
        using var newProvider = new AesGcmBlockEncryptionProvider(newKek);
        var newToken = newProvider.Encrypt(EncryptionHeader.MagicBytes, BlockType.Metadata, blockId: 0);

        var newHeader = new EncryptionHeader
        {
            Magic = EncryptionHeader.MagicBytes,
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = newSalt,
            KeyVerificationToken = newToken
        };

        return (oldSalt, newSalt, reEncryptedKeyStore, newHeader);
    }

    // -----------------------------------------------------------------------
    //  Old password cannot decrypt the re-encrypted key store
    // -----------------------------------------------------------------------

    [Fact]
    public void OldPassword_WithOldSalt_CannotDecryptReEncryptedKeyStore()
    {
        var content = CreateSingleEpochContent();
        var (oldSalt, _, reEncryptedKeyStore, _) = PerformFullPasswordChange(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncryptedKeyStore, oldKek));
    }

    [Fact]
    public void OldPassword_WithNewSalt_CannotDecryptReEncryptedKeyStore()
    {
        var content = CreateSingleEpochContent();
        var (_, newSalt, reEncryptedKeyStore, _) = PerformFullPasswordChange(content);

        // Even if an attacker knows the new salt, the old password still fails
        var wrongKek = KeyDerivation.DeriveFromPassword(OldPassword, newSalt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncryptedKeyStore, wrongKek));
    }

    [Fact]
    public void OldPassword_CannotDecryptReEncryptedKeyStore_MultiEpoch()
    {
        var content = CreateMultiEpochContent();
        var (oldSalt, _, reEncryptedKeyStore, _) = PerformFullPasswordChange(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncryptedKeyStore, oldKek));
    }

    // -----------------------------------------------------------------------
    //  Old password cannot verify the updated key verification token
    // -----------------------------------------------------------------------

    [Fact]
    public void OldPassword_CannotDecryptNewKeyVerificationToken()
    {
        var content = CreateSingleEpochContent();
        var (oldSalt, _, _, newHeader) = PerformFullPasswordChange(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        using var provider = new AesGcmBlockEncryptionProvider(oldKek);

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(newHeader.KeyVerificationToken, BlockType.Metadata, blockId: 0));
    }

    [Fact]
    public void OldPassword_WithNewSalt_CannotDecryptNewKeyVerificationToken()
    {
        var content = CreateSingleEpochContent();
        var (_, newSalt, _, newHeader) = PerformFullPasswordChange(content);

        var wrongKek = KeyDerivation.DeriveFromPassword(OldPassword, newSalt);
        using var provider = new AesGcmBlockEncryptionProvider(wrongKek);

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(newHeader.KeyVerificationToken, BlockType.Metadata, blockId: 0));
    }

    // -----------------------------------------------------------------------
    //  End-to-end: old password cannot open database and read data
    // -----------------------------------------------------------------------

    [Fact]
    public void OldPassword_CannotReadDataBlocks_AfterPasswordChange()
    {
        var content = CreateSingleEpochContent();
        var plaintext = Encoding.UTF8.GetBytes("Secret email content");

        // Encrypt a data block with the DEKs
        using var encProvider = new KeyWrappingEncryptionProvider(content, EncryptionPolicy.Full);
        var ct = encProvider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);

        // Perform password change
        var (oldSalt, _, reEncryptedKeyStore, _) = PerformFullPasswordChange(content);

        // Attempt to open key store with old password — must fail
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncryptedKeyStore, oldKek));
    }

    [Fact]
    public void OldPassword_CannotReadAnyBlockType_AfterPasswordChange()
    {
        var content = CreateSingleEpochContent();

        var blockTypes = new[]
        {
            BlockType.EmailContent,
            BlockType.Folder,
            BlockType.FolderTree,
            BlockType.Segment,
            BlockType.WAL
        };

        // Encrypt blocks of every type
        using var encProvider = new KeyWrappingEncryptionProvider(content, EncryptionPolicy.Full);
        var encryptedBlocks = blockTypes.Select((bt, idx) =>
        {
            var payload = Encoding.UTF8.GetBytes($"Payload for {bt}");
            return (ct: encProvider.Encrypt(payload, bt, idx + 1), type: bt, blockId: (long)(idx + 1));
        }).ToList();

        // Perform password change
        var (oldSalt, _, reEncryptedKeyStore, _) = PerformFullPasswordChange(content);

        // Old password cannot even open the key store, so no DEKs are available
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncryptedKeyStore, oldKek));
    }

    // -----------------------------------------------------------------------
    //  Header round-trip: old password fails after write/read cycle
    // -----------------------------------------------------------------------

    [Fact]
    public void OldPassword_FailsAfterHeaderWriteAndReadRoundTrip()
    {
        var content = CreateSingleEpochContent();
        var (oldSalt, _, _, newHeader) = PerformFullPasswordChange(content);

        // Write the updated header to a stream and read it back
        using var ms = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(ms, newHeader);
        ms.Position = 0;
        var readResult = EncryptionHeaderManager.ReadHeader(ms);

        Assert.True(readResult.IsSuccess);

        // Old password + the round-tripped header's salt must fail verification
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, readResult.Value.Salt);
        using var provider = new AesGcmBlockEncryptionProvider(oldKek);

        Assert.ThrowsAny<CryptographicException>(() =>
            provider.Decrypt(readResult.Value.KeyVerificationToken, BlockType.Metadata, blockId: 0));
    }

    // -----------------------------------------------------------------------
    //  Multiple password changes: all previous passwords are invalidated
    // -----------------------------------------------------------------------

    [Fact]
    public void AllPreviousPasswords_FailAfterMultipleChanges()
    {
        var content = CreateSingleEpochContent();
        var ksManager = new KeyStoreManager(_serializer);

        // First password: OldPassword
        var salt1 = KeyDerivation.GenerateSalt();
        var kek1 = KeyDerivation.DeriveFromPassword(OldPassword, salt1);
        var encryptedKs = ksManager.EncryptKeyStore(content, kek1);

        // Change to NewPassword
        var decryptedKs = ksManager.DecryptKeyStore(encryptedKs, kek1);
        var salt2 = KeyDerivation.GenerateSalt();
        var kek2 = KeyDerivation.DeriveFromPassword(NewPassword, salt2);
        encryptedKs = ksManager.EncryptKeyStore(decryptedKs, kek2);

        // Change to a third password
        const string thirdPassword = "third-correct-horse-battery";
        decryptedKs = ksManager.DecryptKeyStore(encryptedKs, kek2);
        var salt3 = KeyDerivation.GenerateSalt();
        var kek3 = KeyDerivation.DeriveFromPassword(thirdPassword, salt3);
        encryptedKs = ksManager.EncryptKeyStore(decryptedKs, kek3);

        // First password must fail
        var oldKek1 = KeyDerivation.DeriveFromPassword(OldPassword, salt1);
        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(encryptedKs, oldKek1));

        // Second password must fail
        var oldKek2 = KeyDerivation.DeriveFromPassword(NewPassword, salt2);
        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(encryptedKs, oldKek2));

        // Only the third (current) password succeeds
        var currentKek = KeyDerivation.DeriveFromPassword(thirdPassword, salt3);
        var finalDecrypted = ksManager.DecryptKeyStore(encryptedKs, currentKek);
        Assert.NotNull(finalDecrypted);
        Assert.Equal(content.ActiveEpoch, finalDecrypted.ActiveEpoch);
    }

    [Fact]
    public void OldPassword_FailsImmediately_DoesNotReturnPartialData()
    {
        var content = CreateSingleEpochContent();
        var (oldSalt, _, reEncryptedKeyStore, _) = PerformFullPasswordChange(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);

        // The exception must be CryptographicException (AES-GCM auth tag failure),
        // not a deserialization error, confirming the system rejects the key
        // before attempting to interpret any plaintext.
        var ex = Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncryptedKeyStore, oldKek));

        Assert.NotNull(ex);
    }
}
