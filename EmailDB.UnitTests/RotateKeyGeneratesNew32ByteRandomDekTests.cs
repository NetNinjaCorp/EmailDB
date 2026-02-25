using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class RotateKeyGeneratesNew32ByteRandomDekTests
{
    private readonly KeyStoreManager _manager = new(new DefaultBlockContentSerializer());

    private KeyStoreContent CreateSingleEpochKeyStore()
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };
    }

    [Fact]
    public void RotateKey_NewEntryDekIs32Bytes()
    {
        var keyStore = CreateSingleEpochKeyStore();
        _manager.RotateKey(keyStore);

        var newEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.Equal(32, newEntry.DEK.Length);
    }

    [Fact]
    public void RotateKey_NewDekIsDifferentFromOriginal()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var originalDek = keyStore.Entries[0].DEK.ToArray();

        _manager.RotateKey(keyStore);

        var newEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.False(originalDek.AsSpan().SequenceEqual(newEntry.DEK),
            "New DEK should differ from the original DEK.");
    }

    [Fact]
    public void RotateKey_SuccessiveRotationsProduceDifferentDeks()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        var firstRotatedDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK.ToArray();

        _manager.RotateKey(keyStore);
        var secondRotatedDek = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch).DEK.ToArray();

        Assert.False(firstRotatedDek.AsSpan().SequenceEqual(secondRotatedDek),
            "Successive rotations should produce different DEKs.");
    }

    [Fact]
    public void RotateKey_NewDekIsNotAllZeros()
    {
        var keyStore = CreateSingleEpochKeyStore();
        _manager.RotateKey(keyStore);

        var newEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.False(newEntry.DEK.All(b => b == 0),
            "New DEK should not be all zeros (cryptographically random).");
    }

    [Fact]
    public void RotateKey_AddsExactlyOneNewEntry()
    {
        var keyStore = CreateSingleEpochKeyStore();
        int countBefore = keyStore.Entries.Count;

        _manager.RotateKey(keyStore);

        Assert.Equal(countBefore + 1, keyStore.Entries.Count);
    }

    [Fact]
    public void RotateKey_NullKeyStoreThrows()
    {
        Assert.Throws<ArgumentNullException>(() => _manager.RotateKey(null!));
    }

    [Fact]
    public void RotateKey_EmptyEntriesThrows()
    {
        var keyStore = new KeyStoreContent { ActiveEpoch = 0, Entries = new() };
        Assert.Throws<InvalidOperationException>(() => _manager.RotateKey(keyStore));
    }
}
