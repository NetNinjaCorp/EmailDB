using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-57:
/// "Key store block re-encrypted and written"
/// After key rotation, the key store must be re-encrypted with the KEK
/// and the resulting ciphertext must round-trip correctly, preserving the
/// rotated state including the new DEK and updated active epoch.
/// </summary>
public class KeyStoreReEncryptedAfterRotationTests
{
    private const int KeySize = 32;

    private readonly DefaultBlockContentSerializer _serializer = new();
    private readonly KeyStoreManager _manager;

    public KeyStoreReEncryptedAfterRotationTests()
    {
        _manager = new KeyStoreManager(_serializer);
    }

    private static byte[] GenerateKek()
    {
        var kek = new byte[KeySize];
        RandomNumberGenerator.Fill(kek);
        return kek;
    }

    private static byte[] GenerateDek()
    {
        var dek = new byte[KeySize];
        RandomNumberGenerator.Fill(dek);
        return dek;
    }

    private KeyStoreContent CreateSingleEpochKeyStore()
    {
        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new()
                {
                    Epoch = 0,
                    DEK = GenerateDek(),
                    Timestamp = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
                    Retired = false
                }
            }
        };
    }

    [Fact]
    public void RotateThenReEncrypt_DecryptsSuccessfully()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        Assert.NotNull(restored);
        Assert.Equal(keyStore.ActiveEpoch, restored.ActiveEpoch);
    }

    [Fact]
    public void RotateThenReEncrypt_PreservesNewActiveEpoch()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(1, restored.ActiveEpoch);
    }

    [Fact]
    public void RotateThenReEncrypt_PreservesAllEntries()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(2, restored.Entries.Count);
        Assert.Contains(restored.Entries, e => e.Epoch == 0);
        Assert.Contains(restored.Entries, e => e.Epoch == 1);
    }

    [Fact]
    public void RotateThenReEncrypt_NewDekPreservedInRoundTrip()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var newDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK.ToArray();

        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        var restoredNewEntry = restored.Entries.First(e => e.Epoch == restored.ActiveEpoch);
        Assert.Equal(newDek, restoredNewEntry.DEK);
    }

    [Fact]
    public void RotateThenReEncrypt_OriginalDekPreservedInRoundTrip()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();
        var originalDek = keyStore.Entries[0].DEK.ToArray();

        _manager.RotateKey(keyStore);
        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        var restoredOriginal = restored.Entries.First(e => e.Epoch == 0);
        Assert.Equal(originalDek, restoredOriginal.DEK);
    }

    [Fact]
    public void RotateThenReEncrypt_CiphertextDiffersFromPreRotation()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        var encryptedBefore = _manager.EncryptKeyStore(keyStore, kek);

        _manager.RotateKey(keyStore);
        var encryptedAfter = _manager.EncryptKeyStore(keyStore, kek);

        // Re-encrypted block must differ (new DEK entry + random nonce)
        Assert.NotEqual(encryptedBefore, encryptedAfter);
    }

    [Fact]
    public void RotateThenReEncrypt_CiphertextIsLargerAfterRotation()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        var encryptedBefore = _manager.EncryptKeyStore(keyStore, kek);

        _manager.RotateKey(keyStore);
        var encryptedAfter = _manager.EncryptKeyStore(keyStore, kek);

        // More entries means larger serialized payload
        Assert.True(encryptedAfter.Length > encryptedBefore.Length);
    }

    [Fact]
    public void RotateThenReEncrypt_WrongKekFailsDecryption()
    {
        var kek = GenerateKek();
        var wrongKek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var encrypted = _manager.EncryptKeyStore(keyStore, kek);

        Assert.ThrowsAny<CryptographicException>(() =>
            _manager.DecryptKeyStore(encrypted, wrongKek));
    }

    [Fact]
    public void MultipleRotations_ReEncryptedKeyStorePreservesAllEpochs()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);

        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(4, restored.Entries.Count);
        Assert.Equal(3, restored.ActiveEpoch);

        for (int i = 0; i < 4; i++)
        {
            Assert.Contains(restored.Entries, e => e.Epoch == i);
        }
    }

    [Fact]
    public void MultipleRotations_EachReEncryptionIsDecryptable()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        for (int i = 0; i < 5; i++)
        {
            _manager.RotateKey(keyStore);

            var encrypted = _manager.EncryptKeyStore(keyStore, kek);
            var restored = _manager.DecryptKeyStore(encrypted, kek);

            Assert.Equal(keyStore.ActiveEpoch, restored.ActiveEpoch);
            Assert.Equal(keyStore.Entries.Count, restored.Entries.Count);
        }
    }

    [Fact]
    public void RotateThenReEncrypt_RetiredFlagsPreserved()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        var originalEntry = restored.Entries.First(e => e.Epoch == 0);
        var newEntry = restored.Entries.First(e => e.Epoch == 1);

        // Original entry should not be retired (retained, not retired)
        Assert.False(originalEntry.Retired);
        // New entry should not be retired
        Assert.False(newEntry.Retired);
    }

    [Fact]
    public void RotateThenReEncrypt_TimestampsPreserved()
    {
        var kek = GenerateKek();
        var keyStore = CreateSingleEpochKeyStore();
        var originalTimestamp = keyStore.Entries[0].Timestamp;

        _manager.RotateKey(keyStore);
        var newTimestamp = keyStore.Entries.First(e => e.Epoch == 1).Timestamp;

        var encrypted = _manager.EncryptKeyStore(keyStore, kek);
        var restored = _manager.DecryptKeyStore(encrypted, kek);

        Assert.Equal(originalTimestamp, restored.Entries.First(e => e.Epoch == 0).Timestamp);
        Assert.Equal(newTimestamp, restored.Entries.First(e => e.Epoch == 1).Timestamp);
    }
}
