using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class RotateKeyNewEpochActiveInKeyStoreTests
{
    private readonly KeyStoreManager _manager = new(new DefaultBlockContentSerializer());

    private KeyStoreContent CreateSingleEpochKeyStore(int epoch = 0)
    {
        var dek = new byte[32];
        RandomNumberGenerator.Fill(dek);
        return new KeyStoreContent
        {
            ActiveEpoch = epoch,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = epoch, DEK = dek, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };
    }

    private KeyStoreContent CreateMultiEpochKeyStore()
    {
        var dek0 = new byte[32];
        var dek1 = new byte[32];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);
        return new KeyStoreContent
        {
            ActiveEpoch = 1,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = DateTime.UtcNow.AddHours(-2), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = DateTime.UtcNow, Retired = false }
            }
        };
    }

    [Fact]
    public void RotateKey_ActiveEpochMatchesNewEntry()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var activeEntry = keyStore.Entries.FirstOrDefault(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.NotNull(activeEntry);
    }

    [Fact]
    public void RotateKey_ActiveEpochIsNotPreviousEpoch()
    {
        var keyStore = CreateSingleEpochKeyStore();
        int previousEpoch = keyStore.ActiveEpoch;

        _manager.RotateKey(keyStore);

        Assert.NotEqual(previousEpoch, keyStore.ActiveEpoch);
    }

    [Fact]
    public void RotateKey_ActiveEpochPointsToNewestEntry()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        int maxEpoch = keyStore.Entries.Max(e => e.Epoch);
        Assert.Equal(maxEpoch, keyStore.ActiveEpoch);
    }

    [Fact]
    public void RotateKey_ActiveEntryHasValidDek()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var activeEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.NotNull(activeEntry.DEK);
        Assert.Equal(32, activeEntry.DEK.Length);
    }

    [Fact]
    public void RotateKey_ActiveEntryIsNotRetired()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var activeEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.False(activeEntry.Retired);
    }

    [Fact]
    public void RotateKey_SuccessiveRotations_ActiveEpochAlwaysMatchesLatest()
    {
        var keyStore = CreateSingleEpochKeyStore();

        for (int i = 1; i <= 5; i++)
        {
            _manager.RotateKey(keyStore);

            int maxEpoch = keyStore.Entries.Max(e => e.Epoch);
            Assert.Equal(maxEpoch, keyStore.ActiveEpoch);
            Assert.Equal(i, keyStore.ActiveEpoch);
        }
    }

    [Fact]
    public void RotateKey_MultiEpochKeyStore_ActiveEpochUpdated()
    {
        var keyStore = CreateMultiEpochKeyStore();
        Assert.Equal(1, keyStore.ActiveEpoch);

        _manager.RotateKey(keyStore);

        Assert.Equal(2, keyStore.ActiveEpoch);
        var activeEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.NotNull(activeEntry);
    }

    [Fact]
    public void RotateKey_OnlyOneEntryMatchesActiveEpoch()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var activeEntries = keyStore.Entries.Where(e => e.Epoch == keyStore.ActiveEpoch).ToList();
        Assert.Single(activeEntries);
    }

    [Fact]
    public void RotateKey_ActiveEpochEntryExistsInEntries()
    {
        var keyStore = CreateMultiEpochKeyStore();

        _manager.RotateKey(keyStore);
        _manager.RotateKey(keyStore);

        var epochs = keyStore.Entries.Select(e => e.Epoch).ToList();
        Assert.Contains(keyStore.ActiveEpoch, epochs);
    }
}
