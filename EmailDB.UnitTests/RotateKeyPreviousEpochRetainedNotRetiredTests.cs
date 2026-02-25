using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class RotateKeyPreviousEpochRetainedNotRetiredTests
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
    public void RotateKey_PreviousActiveEpochNotRetired()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var previousEntry = keyStore.Entries.First(e => e.Epoch == 0);
        Assert.False(previousEntry.Retired,
            "Previous active epoch should be retained (Retired = false), not retired.");
    }

    [Fact]
    public void RotateKey_PreviousActiveEpochStillExistsInEntries()
    {
        var keyStore = CreateSingleEpochKeyStore();
        int originalEpoch = keyStore.ActiveEpoch;

        _manager.RotateKey(keyStore);

        Assert.Contains(keyStore.Entries, e => e.Epoch == originalEpoch);
    }

    [Fact]
    public void RotateKey_PreviousActiveEpochDekUnchanged()
    {
        var keyStore = CreateSingleEpochKeyStore();
        var originalDek = keyStore.Entries[0].DEK.ToArray();

        _manager.RotateKey(keyStore);

        var previousEntry = keyStore.Entries.First(e => e.Epoch == 0);
        Assert.True(originalDek.AsSpan().SequenceEqual(previousEntry.DEK),
            "Previous epoch DEK should remain unchanged after rotation.");
    }

    [Fact]
    public void RotateKey_MultipleRotations_AllPreviousEpochsRetained()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);

        var previousEntries = keyStore.Entries.Where(e => e.Epoch != keyStore.ActiveEpoch);
        Assert.All(previousEntries, entry =>
            Assert.False(entry.Retired,
                $"Epoch {entry.Epoch} should be retained (Retired = false), not retired."));
    }

    [Fact]
    public void RotateKey_NewEntryIsNotRetired()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var newEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.False(newEntry.Retired,
            "Newly rotated epoch should not be retired.");
    }

    [Fact]
    public void RotateKey_PreviousEntryRetiredFlagExplicitlyFalse()
    {
        var keyStore = CreateSingleEpochKeyStore();
        // Ensure starting state is Retired = false
        Assert.False(keyStore.Entries[0].Retired);

        _manager.RotateKey(keyStore);

        // After rotation, the previous entry should still be Retired = false (retained)
        var previousEntry = keyStore.Entries.First(e => e.Epoch == 0);
        Assert.False(previousEntry.Retired,
            "Previous entry Retired flag should remain false after rotation.");
    }
}
