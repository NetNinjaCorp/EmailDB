using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class BlockFlagEncryptedTests
{
    [Fact]
    public void FlagEncrypted_Constant_Equals0x01()
    {
        Assert.Equal(0x01, Block.FlagEncrypted);
    }

    [Fact]
    public void FlagEncrypted_IsPublicConst()
    {
        var field = typeof(Block).GetField("FlagEncrypted");
        Assert.NotNull(field);
        Assert.True(field.IsPublic);
        Assert.True(field.IsLiteral);  // const fields are literal
        Assert.Equal(typeof(byte), field.FieldType);
    }

    [Fact]
    public void IsEncrypted_ReturnsFalse_WhenFlagsIsZero()
    {
        var block = new Block { Flags = 0x00 };
        Assert.False(block.IsEncrypted);
    }

    [Fact]
    public void IsEncrypted_ReturnsTrue_WhenFlagEncryptedBitIsSet()
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        Assert.True(block.IsEncrypted);
    }

    [Fact]
    public void IsEncrypted_ReturnsTrue_WhenMultipleFlagsIncludeEncrypted()
    {
        var block = new Block { Flags = (byte)(Block.FlagEncrypted | 0x02) };
        Assert.True(block.IsEncrypted);
    }

    [Fact]
    public void IsEncrypted_ReturnsFalse_WhenOtherFlagsSetButNotEncrypted()
    {
        var block = new Block { Flags = 0x02 };
        Assert.False(block.IsEncrypted);
    }

    [Fact]
    public void IsEncrypted_IsReadOnlyProperty()
    {
        var property = typeof(Block).GetProperty("IsEncrypted");
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.True(property.CanRead);
        Assert.False(property.CanWrite);
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x01, true)]
    [InlineData(0x02, false)]
    [InlineData(0x03, true)]
    [InlineData(0xFF, true)]
    [InlineData(0xFE, false)]
    public void IsEncrypted_CorrectlyReadsBit0(byte flags, bool expected)
    {
        var block = new Block { Flags = flags };
        Assert.Equal(expected, block.IsEncrypted);
    }
}
