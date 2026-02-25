using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class KeyStoreContentModelTests
{
    [Fact]
    public void KeyStoreContent_HasActiveEpochProperty()
    {
        var content = new KeyStoreContent();
        content.ActiveEpoch = 3;
        Assert.Equal(3, content.ActiveEpoch);
    }

    [Fact]
    public void KeyStoreContent_ActiveEpoch_DefaultsToZero()
    {
        var content = new KeyStoreContent();
        Assert.Equal(0, content.ActiveEpoch);
    }

    [Fact]
    public void KeyStoreContent_Entries_DefaultsToEmptyList()
    {
        var content = new KeyStoreContent();
        Assert.NotNull(content.Entries);
        Assert.Empty(content.Entries);
    }

    [Fact]
    public void KeyStoreEntry_HasEpochProperty()
    {
        var entry = new KeyStoreEntry { Epoch = 5 };
        Assert.Equal(5, entry.Epoch);
    }

    [Fact]
    public void KeyStoreEntry_HasDEKProperty()
    {
        var dek = new byte[32];
        Array.Fill(dek, (byte)0xAB);
        var entry = new KeyStoreEntry { DEK = dek };

        Assert.Equal(32, entry.DEK.Length);
        Assert.All(entry.DEK, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void KeyStoreEntry_DEK_DefaultsToEmptyArray()
    {
        var entry = new KeyStoreEntry();
        Assert.NotNull(entry.DEK);
        Assert.Empty(entry.DEK);
    }

    [Fact]
    public void KeyStoreEntry_HasTimestampProperty()
    {
        var now = DateTime.UtcNow;
        var entry = new KeyStoreEntry { Timestamp = now };
        Assert.Equal(now, entry.Timestamp);
    }

    [Fact]
    public void KeyStoreEntry_HasRetiredProperty()
    {
        var entry = new KeyStoreEntry { Retired = true };
        Assert.True(entry.Retired);
    }

    [Fact]
    public void KeyStoreEntry_Retired_DefaultsToFalse()
    {
        var entry = new KeyStoreEntry();
        Assert.False(entry.Retired);
    }

    [Fact]
    public void KeyStoreContent_CanAddMultipleEntries()
    {
        var content = new KeyStoreContent { ActiveEpoch = 1 };
        content.Entries.Add(new KeyStoreEntry
        {
            Epoch = 0,
            DEK = new byte[32],
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Retired = true
        });
        content.Entries.Add(new KeyStoreEntry
        {
            Epoch = 1,
            DEK = new byte[32],
            Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Retired = false
        });

        Assert.Equal(2, content.Entries.Count);
        Assert.True(content.Entries[0].Retired);
        Assert.False(content.Entries[1].Retired);
    }

    [Fact]
    public void KeyStoreContent_ActiveEpoch_CorrespondsToEntry()
    {
        var content = new KeyStoreContent { ActiveEpoch = 2 };
        content.Entries.Add(new KeyStoreEntry { Epoch = 0, Retired = true });
        content.Entries.Add(new KeyStoreEntry { Epoch = 1, Retired = true });
        content.Entries.Add(new KeyStoreEntry { Epoch = 2, Retired = false });

        var activeEntry = content.Entries.First(e => e.Epoch == content.ActiveEpoch);
        Assert.False(activeEntry.Retired);
    }
}
