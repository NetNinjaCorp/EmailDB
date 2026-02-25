using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "Decrypts key store with old KEK"
///
/// During a password change, the system must use the old KEK to decrypt
/// the existing encrypted key store, recovering the full DEK table so it
/// can be re-encrypted with the new KEK.
/// </summary>
public class ChangePasswordDecryptsKeyStoreWithOldKekTests
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
    /// Simulates the on-disk state: key store encrypted with KEK derived from old password.
    /// Returns the salt, encrypted key store blob, and original content for verification.
    /// </summary>
    private (byte[] salt, byte[] encryptedKeyStore, KeyStoreContent original) SetupEncryptedKeyStore(
        KeyStoreContent content)
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveFromPassword(OldPassword, salt);

        var ksManager = new KeyStoreManager(_serializer);
        var encrypted = ksManager.EncryptKeyStore(content, kek);

        return (salt, encrypted, content);
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_ReturnsNonNull()
    {
        var content = CreateSingleEpochContent();
        var (salt, encryptedKeyStore, _) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);

        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.NotNull(decrypted);
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_RecoversSingleDek()
    {
        var content = CreateSingleEpochContent();
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.Single(decrypted.Entries);
        Assert.Equal(original.Entries[0].DEK, decrypted.Entries[0].DEK);
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_RecoverActiveEpoch()
    {
        var content = CreateMultiEpochContent();
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.Equal(original.ActiveEpoch, decrypted.ActiveEpoch);
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_RecoversAllDekEntries()
    {
        var content = CreateMultiEpochContent();
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.Equal(original.Entries.Count, decrypted.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Epoch, decrypted.Entries[i].Epoch);
            Assert.Equal(original.Entries[i].DEK, decrypted.Entries[i].DEK);
            Assert.Equal(original.Entries[i].Retired, decrypted.Entries[i].Retired);
        }
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_RecoveredDeksAreUsable()
    {
        // After decryption the recovered DEKs must be valid 32-byte keys
        // that can be loaded into a KeyWrappingEncryptionProvider.
        var content = CreateSingleEpochContent();
        var (salt, encryptedKeyStore, _) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        var policy = EncryptionPolicy.Full;
        using var provider = new KeyWrappingEncryptionProvider(decrypted, policy);

        Assert.True(provider.IsEnabled);
        Assert.Equal(decrypted.ActiveEpoch, provider.ActiveEpoch);
    }

    [Fact]
    public void WrongOldPassword_FailsToDecryptKeyStore()
    {
        var content = CreateSingleEpochContent();
        var (salt, encryptedKeyStore, _) = SetupEncryptedKeyStore(content);

        var wrongKek = KeyDerivation.DeriveFromPassword("wrong-password", salt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(encryptedKeyStore, wrongKek));
    }

    [Fact]
    public void NewPassword_CannotDecryptKeyStoreEncryptedWithOldPassword()
    {
        // Confirms the new password's KEK cannot decrypt the old key store —
        // the system MUST use the old KEK first.
        var content = CreateSingleEpochContent();
        var (salt, encryptedKeyStore, _) = SetupEncryptedKeyStore(content);

        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(encryptedKeyStore, newKek));
    }

    [Fact]
    public void DecryptedKeyStore_CanBeReEncryptedWithNewKek()
    {
        // End-to-end: old KEK decrypts, then new KEK re-encrypts successfully.
        var content = CreateMultiEpochContent();
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore(content);

        // Step 1: Decrypt with old KEK
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        // Step 2: Re-encrypt with new KEK
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);

        // Step 3: Verify new KEK decrypts to identical DEK table
        var reDecrypted = ksManager.DecryptKeyStore(reEncrypted, newKek);

        Assert.Equal(original.ActiveEpoch, reDecrypted.ActiveEpoch);
        Assert.Equal(original.Entries.Count, reDecrypted.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].DEK, reDecrypted.Entries[i].DEK);
            Assert.Equal(original.Entries[i].Epoch, reDecrypted.Entries[i].Epoch);
        }
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_RetiredFlagsPreserved()
    {
        var content = CreateMultiEpochContent();
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        Assert.True(decrypted.Entries[0].Retired);
        Assert.True(decrypted.Entries[1].Retired);
        Assert.False(decrypted.Entries[2].Retired);
    }

    [Fact]
    public void OldKek_DecryptsKeyStore_TimestampsPreserved()
    {
        var content = CreateMultiEpochContent();
        var (salt, encryptedKeyStore, original) = SetupEncryptedKeyStore(content);

        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, salt);
        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Timestamp, decrypted.Entries[i].Timestamp);
        }
    }
}
