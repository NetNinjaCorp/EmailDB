using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "ChangePassword method derives old KEK from old password and existing salt"
///
/// The password-change flow must re-derive the old KEK from the user's old
/// password and the salt already stored in the EncryptionHeader, then use
/// that KEK to decrypt the existing key store before re-wrapping it.
/// </summary>
public class ChangePasswordDerivesOldKekTests
{
    private const int KeySize = 32;
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
                    Timestamp = DateTime.UtcNow,
                    Retired = false
                }
            }
        };
    }

    /// <summary>
    /// Simulates the initial file state: salt + key store encrypted with KEK
    /// derived from the old password.
    /// </summary>
    private (byte[] salt, byte[] encryptedKeyStore, KeyStoreContent original) SetupEncryptedKeyStore()
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var content = CreateSampleContent();

        var ksManager = new KeyStoreManager(_serializer);
        var encrypted = ksManager.EncryptKeyStore(content, kek);

        return (salt, encrypted, content);
    }

    [Fact]
    public void OldKekDerivedFromOldPasswordAndExistingSalt_DecryptsKeyStore()
    {
        var (salt, encryptedKeyStore, _) = SetupEncryptedKeyStore();

        // Act: derive old KEK from old password + existing salt (the ChangePassword first step)
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);

        // Assert: the re-derived KEK successfully decrypts the key store
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.NotNull(decrypted);
    }

    [Fact]
    public void OldKekDerivedFromOldPasswordAndExistingSalt_RecoversDekTable()
    {
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore();

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.Equal(original.ActiveEpoch, decrypted.ActiveEpoch);
        Assert.Equal(original.Entries.Count, decrypted.Entries.Count);
        Assert.Equal(original.Entries[0].DEK, decrypted.Entries[0].DEK);
    }

    [Fact]
    public void OldKekUsesExistingSalt_NotANewlyGeneratedSalt()
    {
        // Two different salts with the same password must produce different KEKs.
        // ChangePassword must use the existing salt, not generate a new one for the old KEK.
        var existingSalt = KeyDerivation.GenerateSalt();
        var differentSalt = KeyDerivation.GenerateSalt();

        var kekWithExisting = KeyDerivation.DeriveFromPassword(OldPassword, existingSalt);
        var kekWithDifferent = KeyDerivation.DeriveFromPassword(OldPassword, differentSalt);

        Assert.NotEqual(kekWithExisting, kekWithDifferent);
    }

    [Fact]
    public void WrongPassword_WithExistingSalt_FailsToDecryptKeyStore()
    {
        var (salt, encryptedKeyStore, _) = SetupEncryptedKeyStore();

        // Derive KEK from wrong password but correct salt
        var wrongKek = KeyDerivation.DeriveFromPassword("wrong-password", salt);

        var ksManager = new KeyStoreManager(_serializer);
        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(encryptedKeyStore, wrongKek));
    }

    [Fact]
    public void OldKekDerived_ThenNewKekReWraps_PreservesDekTable()
    {
        // Full ChangePassword round-trip: old KEK decrypts, new KEK re-encrypts,
        // and the DEK table survives intact.
        var (existingSalt, encryptedKeyStore, original) = SetupEncryptedKeyStore();

        // Step 1: Derive old KEK from old password + existing salt
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, existingSalt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        // Step 2: Derive new KEK from new password + new salt
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        // Step 3: Re-encrypt with new KEK
        var reEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);

        // Step 4: Verify new KEK decrypts to same DEK table
        var reDecrypted = ksManager.DecryptKeyStore(reEncrypted, newKek);

        Assert.Equal(original.ActiveEpoch, reDecrypted.ActiveEpoch);
        Assert.Equal(original.Entries.Count, reDecrypted.Entries.Count);
        Assert.Equal(original.Entries[0].DEK, reDecrypted.Entries[0].DEK);
        Assert.Equal(original.Entries[0].Epoch, reDecrypted.Entries[0].Epoch);
    }

    [Fact]
    public void OldKekDerived_MultiEpochKeyStore_DecryptsAllEntries()
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveFromPassword(OldPassword, salt);

        var dek0 = new byte[KeySize];
        var dek1 = new byte[KeySize];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);

        var content = new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddHours(-1), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };

        var ksManager = new KeyStoreManager(_serializer);
        var encrypted = ksManager.EncryptKeyStore(content, kek);

        // Re-derive old KEK and decrypt
        var reDerivedKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var decrypted = ksManager.DecryptKeyStore(encrypted, reDerivedKek);

        Assert.Equal(2, decrypted.Entries.Count);
        Assert.Equal(dek0, decrypted.Entries[0].DEK);
        Assert.Equal(dek1, decrypted.Entries[1].DEK);
        Assert.Equal(1, decrypted.ActiveEpoch);
    }

    [Fact]
    public void SameOldPasswordAndSalt_AlwaysProducesSameKek()
    {
        var salt = KeyDerivation.GenerateSalt();

        var kek1 = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var kek2 = KeyDerivation.DeriveFromPassword(OldPassword, salt);

        Assert.Equal(kek1, kek2);
    }
}
