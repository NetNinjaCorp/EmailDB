using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "Re-encrypts key store with new KEK"
///
/// During a password change, after decrypting the key store with the old KEK
/// and deriving a new KEK from the new password, the system must re-encrypt
/// the key store with the new KEK so that subsequent opens use the new password.
/// </summary>
public class ChangePasswordReEncryptsKeyStoreWithNewKekTests
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
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
    /// Simulates the full password-change re-encryption flow:
    /// encrypt with old KEK -> decrypt with old KEK -> re-encrypt with new KEK.
    /// Returns the original content, old encrypted blob, and new encrypted blob.
    /// </summary>
    private (KeyStoreContent original, byte[] oldEncrypted, byte[] newEncrypted, byte[] newKek)
        PerformReEncryption(KeyStoreContent content)
    {
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        var ksManager = new KeyStoreManager(_serializer);
        var oldEncrypted = ksManager.EncryptKeyStore(content, oldKek);

        // Decrypt with old KEK (as ChangePassword would)
        var decrypted = ksManager.DecryptKeyStore(oldEncrypted, oldKek);

        // Derive new KEK and re-encrypt
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var newEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);

        return (content, oldEncrypted, newEncrypted, newKek);
    }

    [Fact]
    public void ReEncryptedKeyStore_IsNotNull()
    {
        var content = CreateSingleEpochContent();
        var (_, _, newEncrypted, _) = PerformReEncryption(content);

        Assert.NotNull(newEncrypted);
    }

    [Fact]
    public void ReEncryptedKeyStore_IsNotEmpty()
    {
        var content = CreateSingleEpochContent();
        var (_, _, newEncrypted, _) = PerformReEncryption(content);

        Assert.True(newEncrypted.Length > 0);
    }

    [Fact]
    public void ReEncryptedKeyStore_DiffersFromOriginalEncrypted()
    {
        // The re-encrypted blob must differ from the original because it uses
        // a different KEK and a fresh random nonce.
        var content = CreateSingleEpochContent();
        var (_, oldEncrypted, newEncrypted, _) = PerformReEncryption(content);

        Assert.NotEqual(oldEncrypted, newEncrypted);
    }

    [Fact]
    public void ReEncryptedKeyStore_HasCorrectFormat_NonceAndTag()
    {
        // Output format: [Nonce (12)] [Ciphertext (N)] [Auth Tag (16)]
        // Minimum length is NonceSize + TagSize with at least 1 byte of ciphertext.
        var content = CreateSingleEpochContent();
        var (_, _, newEncrypted, _) = PerformReEncryption(content);

        Assert.True(newEncrypted.Length > NonceSize + TagSize);
    }

    [Fact]
    public void ReEncryptedKeyStore_DecryptableWithNewKek()
    {
        var content = CreateSingleEpochContent();
        var (_, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        Assert.NotNull(decrypted);
    }

    [Fact]
    public void ReEncryptedKeyStore_PreservesActiveEpoch_SingleEpoch()
    {
        var content = CreateSingleEpochContent();
        var (original, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        Assert.Equal(original.ActiveEpoch, decrypted.ActiveEpoch);
    }

    [Fact]
    public void ReEncryptedKeyStore_PreservesActiveEpoch_MultiEpoch()
    {
        var content = CreateMultiEpochContent();
        var (original, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        Assert.Equal(original.ActiveEpoch, decrypted.ActiveEpoch);
    }

    [Fact]
    public void ReEncryptedKeyStore_PreservesDekBytes_SingleEpoch()
    {
        var content = CreateSingleEpochContent();
        var (original, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        Assert.Equal(original.Entries[0].DEK, decrypted.Entries[0].DEK);
    }

    [Fact]
    public void ReEncryptedKeyStore_PreservesAllDekEntries_MultiEpoch()
    {
        var content = CreateMultiEpochContent();
        var (original, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        Assert.Equal(original.Entries.Count, decrypted.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Epoch, decrypted.Entries[i].Epoch);
            Assert.Equal(original.Entries[i].DEK, decrypted.Entries[i].DEK);
        }
    }

    [Fact]
    public void ReEncryptedKeyStore_PreservesRetiredFlags()
    {
        var content = CreateMultiEpochContent();
        var (original, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Retired, decrypted.Entries[i].Retired);
        }
    }

    [Fact]
    public void ReEncryptedKeyStore_PreservesTimestamps()
    {
        var content = CreateMultiEpochContent();
        var (original, _, newEncrypted, newKek) = PerformReEncryption(content);

        var ksManager = new KeyStoreManager(_serializer);
        var decrypted = ksManager.DecryptKeyStore(newEncrypted, newKek);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            Assert.Equal(original.Entries[i].Timestamp, decrypted.Entries[i].Timestamp);
        }
    }

    [Fact]
    public void OldKek_CannotDecryptReEncryptedKeyStore()
    {
        var content = CreateSingleEpochContent();
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        var ksManager = new KeyStoreManager(_serializer);
        var oldEncrypted = ksManager.EncryptKeyStore(content, oldKek);
        var decrypted = ksManager.DecryptKeyStore(oldEncrypted, oldKek);

        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncrypted = ksManager.EncryptKeyStore(decrypted, newKek);

        Assert.ThrowsAny<CryptographicException>(() =>
            ksManager.DecryptKeyStore(reEncrypted, oldKek));
    }

    [Fact]
    public void ReEncryptedKeyStore_UsesFreshNonce_DifferentCiphertextEachTime()
    {
        // Two re-encryptions with the same new KEK must produce different ciphertext
        // because each call generates a fresh random nonce.
        var content = CreateSingleEpochContent();
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        var ksManager = new KeyStoreManager(_serializer);
        var oldEncrypted = ksManager.EncryptKeyStore(content, oldKek);
        var decrypted = ksManager.DecryptKeyStore(oldEncrypted, oldKek);

        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);

        var reEncrypted1 = ksManager.EncryptKeyStore(decrypted, newKek);
        var reEncrypted2 = ksManager.EncryptKeyStore(decrypted, newKek);

        Assert.NotEqual(reEncrypted1, reEncrypted2);
    }
}
