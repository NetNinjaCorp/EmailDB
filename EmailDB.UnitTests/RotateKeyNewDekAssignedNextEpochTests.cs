using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests;

public class RotateKeyNewDekAssignedNextEpochTests
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
    public void RotateKey_NewEpochIsMaxPlusOne_SingleEntry()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        var newEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.Equal(1, newEntry.Epoch);
    }

    [Fact]
    public void RotateKey_NewEpochIsMaxPlusOne_MultipleEntries()
    {
        var keyStore = CreateMultiEpochKeyStore();

        _manager.RotateKey(keyStore);

        var newEntry = keyStore.Entries.First(e => e.Epoch == keyStore.ActiveEpoch);
        Assert.Equal(2, newEntry.Epoch);
    }

    [Fact]
    public void RotateKey_ActiveEpochUpdatedToNewEpoch()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);

        Assert.Equal(1, keyStore.ActiveEpoch);
    }

    [Fact]
    public void RotateKey_SuccessiveRotationsIncrementEpoch()
    {
        var keyStore = CreateSingleEpochKeyStore();

        _manager.RotateKey(keyStore);
        Assert.Equal(1, keyStore.ActiveEpoch);

        _manager.RotateKey(keyStore);
        Assert.Equal(2, keyStore.ActiveEpoch);

        _manager.RotateKey(keyStore);
        Assert.Equal(3, keyStore.ActiveEpoch);
    }

    [Fact]
    public void RotateKey_EpochsAreMonotonicallyIncreasing()
    {
        var keyStore = CreateSingleEpochKeyStore();

        for (int i = 0; i < 5; i++)
            _manager.RotateKey(keyStore);

        var epochs = keyStore.Entries.Select(e => e.Epoch).OrderBy(e => e).ToList();
        Assert.Equal(new List<int> { 0, 1, 2, 3, 4, 5 }, epochs);
    }

    [Fact]
    public void RotateKey_PreviousEpochsUnchanged()
    {
        var keyStore = CreateMultiEpochKeyStore();
        var originalEpochs = keyStore.Entries.Select(e => e.Epoch).ToList();

        _manager.RotateKey(keyStore);

        var existingEpochs = keyStore.Entries.Take(2).Select(e => e.Epoch).ToList();
        Assert.Equal(originalEpochs, existingEpochs);
    }

    [Fact]
    public void RotateKey_NewEntryHasCorrectEpochAssigned()
    {
        var keyStore = CreateSingleEpochKeyStore();
        int maxEpochBefore = keyStore.Entries.Max(e => e.Epoch);

        _manager.RotateKey(keyStore);

        var lastEntry = keyStore.Entries.Last();
        Assert.Equal(maxEpochBefore + 1, lastEntry.Epoch);
    }
}
