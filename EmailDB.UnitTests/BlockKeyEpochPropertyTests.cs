using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class BlockKeyEpochPropertyTests
{
    [Fact]
    public void KeyEpoch_ReturnsZero_WhenFlagsIsZero()
    {
        var block = new Block { Flags = 0x00 };
        Assert.Equal(0, block.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_ReturnsZero_WhenOnlyEncryptedBitSet()
    {
        // Flags = 0b0000_0001 => bit 0 set, bits 1-7 = 0
        var block = new Block { Flags = 0x01 };
        Assert.Equal(0, block.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_ReturnsOne_WhenBit1Set()
    {
        // Flags = 0b0000_0010 => epoch = 1
        var block = new Block { Flags = 0x02 };
        Assert.Equal(1, block.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_ReturnsOne_WhenBit1AndEncryptedSet()
    {
        // Flags = 0b0000_0011 => encrypted + epoch 1
        var block = new Block { Flags = 0x03 };
        Assert.Equal(1, block.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_ReturnsMaxValue127_WhenBits1Through7AllSet()
    {
        // Flags = 0b1111_1110 => epoch = 127, not encrypted
        var block = new Block { Flags = 0xFE };
        Assert.Equal(127, block.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_ReturnsMaxValue127_WhenAllBitsSet()
    {
        // Flags = 0xFF => encrypted + epoch 127
        var block = new Block { Flags = 0xFF };
        Assert.Equal(127, block.KeyEpoch);
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 0)]   // only encrypted bit
    [InlineData(0x02, 1)]   // epoch 1, no encryption
    [InlineData(0x03, 1)]   // epoch 1, encrypted
    [InlineData(0x0A, 5)]   // epoch 5, no encryption (0b0000_1010)
    [InlineData(0x0B, 5)]   // epoch 5, encrypted     (0b0000_1011)
    [InlineData(0x40, 32)]  // epoch 32               (0b0100_0000)
    [InlineData(0x80, 64)]  // epoch 64               (0b1000_0000)
    [InlineData(0xFE, 127)] // epoch 127, not encrypted
    [InlineData(0xFF, 127)] // epoch 127, encrypted
    public void KeyEpoch_CorrectlyReadsBits1Through7(byte flags, byte expectedEpoch)
    {
        var block = new Block { Flags = flags };
        Assert.Equal(expectedEpoch, block.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_IgnoresBit0()
    {
        // Two blocks that differ only in bit 0 should have the same KeyEpoch
        var blockA = new Block { Flags = 0x0A }; // 0b0000_1010
        var blockB = new Block { Flags = 0x0B }; // 0b0000_1011
        Assert.Equal(blockA.KeyEpoch, blockB.KeyEpoch);
    }

    [Fact]
    public void KeyEpoch_IsReadOnlyProperty()
    {
        var property = typeof(Block).GetProperty("KeyEpoch");
        Assert.NotNull(property);
        Assert.Equal(typeof(byte), property.PropertyType);
        Assert.True(property.CanRead);
        Assert.False(property.CanWrite);
    }

    [Fact]
    public void KeyEpoch_MatchesExplicitBitShiftFormula()
    {
        // Verify for every possible Flags value that KeyEpoch == (Flags >> 1) & 0x7F
        for (int f = 0; f <= 0xFF; f++)
        {
            var block = new Block { Flags = (byte)f };
            byte expected = (byte)((f >> 1) & 0x7F);
            Assert.Equal(expected, block.KeyEpoch);
        }
    }
}
