using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "Generates new salt and derives new KEK from new password"
///
/// During a password change, the system must generate a fresh random salt
/// and use it together with the new password to derive a new KEK.  This
/// new KEK is then used to re-encrypt the key store.
/// </summary>
public class ChangePasswordGeneratesNewSaltAndNewKekTests
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
    /// Simulates the on-disk state: key store encrypted with KEK derived from old password.
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
    public void NewSalt_IsDifferentFromExistingSalt()
    {
        var (existingSalt, _, _) = SetupEncryptedKeyStore();

        // ChangePassword step: generate a new salt for the new password
        var newSalt = KeyDerivation.GenerateSalt();

        Assert.NotEqual(existingSalt, newSalt);
    }

    [Fact]
    public void NewSalt_IsCorrectLength()
    {
        var newSalt = KeyDerivation.GenerateSalt();

        Assert.Equal(KeyDerivation.SaltSize, newSalt.Length);
    }

    [Fact]
    public void NewSalt_IsNotAllZeros()
    {
        // Cryptographically random salt should not be all zeros
        var newSalt = KeyDerivation.GenerateSalt();

        Assert.False(newSalt.All(b => b == 0));
    }

    [Fact]
    public void MultipleSaltGenerations_ProduceDifferentValues()
    {
        // Each call to GenerateSalt must produce a unique value
        var salts = Enumerable.Range(0, 10)
            .Select(_ => KeyDerivation.GenerateSalt())
            .ToList();

        for (int i = 0; i < salts.Count; i++)
        {
            for (int j = i + 1; j < salts.Count; j++)
            {
                Assert.NotEqual(salts[i], salts[j]);
            }
        }
    }

    [Fact]
    public void NewKek_DerivedFromNewPasswordAndNewSalt_IsValidKeySize()
    {
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        Assert.Equal(KeyDerivation.KeySize, newKek.Length);
    }

    [Fact]
    public void NewKek_IsDifferentFromOldKek()
    {
        var (existingSalt, _, _) = SetupEncryptedKeyStore();

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, existingSalt);

        // ChangePassword: generate new salt and derive new KEK
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        Assert.NotEqual(oldKek, newKek);
    }

    [Fact]
    public void NewKek_WithSameNewPassword_ButDifferentSalts_ProducesDifferentKeks()
    {
        // Even with the same new password, different salts must yield different KEKs.
        var salt1 = KeyDerivation.GenerateSalt();
        var salt2 = KeyDerivation.GenerateSalt();

        var kek1 = KeyDerivation.DeriveFromPassword(NewPassword, salt1);
        var kek2 = KeyDerivation.DeriveFromPassword(NewPassword, salt2);

        Assert.NotEqual(kek1, kek2);
    }

    [Fact]
    public void NewKek_CanEncryptKeyStore()
    {
        var (existingSalt, encryptedKeyStore, original) = SetupEncryptedKeyStore();

        // Step 1: Decrypt with old KEK
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, existingSalt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        // Step 2: Generate new salt and derive new KEK
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        // Step 3: Re-encrypt with new KEK — should not throw
        var reEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);

        Assert.NotNull(reEncrypted);
        Assert.True(reEncrypted.Length > 0);
    }

    [Fact]
    public void NewKek_DecryptsReEncryptedKeyStore_PreservesDekTable()
    {
        var (existingSalt, encryptedKeyStore, original) = SetupEncryptedKeyStore();

        // Step 1: Decrypt with old KEK
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, existingSalt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        // Step 2: Generate new salt and derive new KEK
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        // Step 3: Re-encrypt and decrypt with new KEK
        var reEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);
        var reDecrypted = ksManager.DecryptKeyStore(reEncrypted, newKek);

        Assert.Equal(original.ActiveEpoch, reDecrypted.ActiveEpoch);
        Assert.Equal(original.Entries.Count, reDecrypted.Entries.Count);
        Assert.Equal(original.Entries[0].DEK, reDecrypted.Entries[0].DEK);
        Assert.Equal(original.Entries[0].Epoch, reDecrypted.Entries[0].Epoch);
    }

    [Fact]
    public void OldKek_CannotDecryptKeyStoreReEncryptedWithNewKek()
    {
        var (existingSalt, encryptedKeyStore, _) = SetupEncryptedKeyStore();

        // Decrypt with old KEK
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, existingSalt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        // Generate new salt, derive new KEK, re-encrypt
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);

        // Old KEK must NOT be able to decrypt the re-encrypted key store
        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncrypted, oldKek));
    }

    [Fact]
    public void NewKek_DerivedFromNewPasswordAndNewSalt_IsDeterministic()
    {
        // Same new password + same new salt must produce the same KEK every time
        var newSalt = KeyDerivation.GenerateSalt();

        var kek1 = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var kek2 = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        Assert.Equal(kek1, kek2);
    }
}
