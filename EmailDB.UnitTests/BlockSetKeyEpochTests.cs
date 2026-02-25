using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class BlockSetKeyEpochTests
{
    [Fact]
    public void SetKeyEpoch_WritesEpochIntoBits1Through7()
    {
        var block = new Block { Flags = 0x00 };
        block.SetKeyEpoch(5);
        // epoch 5 => 0b0000_1010
        Assert.Equal(0x0A, block.Flags);
        Assert.Equal(5, block.KeyEpoch);
    }

    [Fact]
    public void SetKeyEpoch_PreservesBit0_WhenEncryptedBitSet()
    {
        var block = new Block { Flags = Block.FlagEncrypted }; // bit 0 = 1
        block.SetKeyEpoch(5);
        // epoch 5 with encrypted => 0b0000_1011
        Assert.Equal(0x0B, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(5, block.KeyEpoch);
    }

    [Fact]
    public void SetKeyEpoch_PreservesBit0_WhenEncryptedBitClear()
    {
        var block = new Block { Flags = 0x00 }; // bit 0 = 0
        block.SetKeyEpoch(10);
        Assert.False(block.IsEncrypted);
        Assert.Equal(10, block.KeyEpoch);
    }

    [Fact]
    public void SetKeyEpoch_Zero_ClearsBits1Through7_PreservesBit0()
    {
        var block = new Block { Flags = 0xFF }; // all bits set (encrypted + epoch 127)
        block.SetKeyEpoch(0);
        // Should keep bit 0 set, clear bits 1-7
        Assert.Equal(0x01, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
    }

    [Fact]
    public void SetKeyEpoch_MaxValue127_SetsAllEpochBits()
    {
        var block = new Block { Flags = 0x00 };
        block.SetKeyEpoch(127);
        // epoch 127 => 0b1111_1110
        Assert.Equal(0xFE, block.Flags);
        Assert.Equal(127, block.KeyEpoch);
    }

    [Fact]
    public void SetKeyEpoch_MaxValue127_WithEncryptedBit()
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(127);
        Assert.Equal(0xFF, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(127, block.KeyEpoch);
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(1, 0x02)]
    [InlineData(5, 0x0A)]
    [InlineData(32, 0x40)]
    [InlineData(64, 0x80)]
    [InlineData(127, 0xFE)]
    public void SetKeyEpoch_ProducesCorrectFlags_WhenNotEncrypted(byte epoch, byte expectedFlags)
    {
        var block = new Block { Flags = 0x00 };
        block.SetKeyEpoch(epoch);
        Assert.Equal(expectedFlags, block.Flags);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x03)]
    [InlineData(5, 0x0B)]
    [InlineData(32, 0x41)]
    [InlineData(64, 0x81)]
    [InlineData(127, 0xFF)]
    public void SetKeyEpoch_ProducesCorrectFlags_WhenEncrypted(byte epoch, byte expectedFlags)
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(epoch);
        Assert.Equal(expectedFlags, block.Flags);
    }

    [Fact]
    public void SetKeyEpoch_ThrowsForValueAbove127()
    {
        var block = new Block();
        Assert.Throws<ArgumentOutOfRangeException>(() => block.SetKeyEpoch(128));
    }

    [Fact]
    public void SetKeyEpoch_OverwritesPreviousEpoch()
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(10);
        Assert.Equal(10, block.KeyEpoch);

        block.SetKeyEpoch(42);
        Assert.Equal(42, block.KeyEpoch);
        Assert.True(block.IsEncrypted); // bit 0 still preserved
    }

    [Fact]
    public void SetKeyEpoch_RoundTripsWithKeyEpochProperty()
    {
        // Verify that for every valid epoch, SetKeyEpoch followed by KeyEpoch returns the same value
        for (byte epoch = 0; epoch <= 127; epoch++)
        {
            var block = new Block { Flags = 0x00 };
            block.SetKeyEpoch(epoch);
            Assert.Equal(epoch, block.KeyEpoch);
        }
    }

    [Fact]
    public void SetKeyEpoch_RoundTripsWithKeyEpochProperty_WhenEncrypted()
    {
        for (byte epoch = 0; epoch <= 127; epoch++)
        {
            var block = new Block { Flags = Block.FlagEncrypted };
            block.SetKeyEpoch(epoch);
            Assert.Equal(epoch, block.KeyEpoch);
            Assert.True(block.IsEncrypted);
        }
    }
}
