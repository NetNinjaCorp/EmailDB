using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class BlockChecksumPropertyTypeTests
{
    [Fact]
    public void HeaderChecksum_IsBytes_NotUint()
    {
        var prop = typeof(Block).GetProperty(nameof(Block.HeaderChecksum));
        Assert.NotNull(prop);
        Assert.Equal(typeof(byte[]), prop!.PropertyType);
    }

    [Fact]
    public void PayloadChecksum_IsBytes_NotUint()
    {
        var prop = typeof(Block).GetProperty(nameof(Block.PayloadChecksum));
        Assert.NotNull(prop);
        Assert.Equal(typeof(byte[]), prop!.PropertyType);
    }

    [Fact]
    public void HeaderChecksum_DefaultsToNull_CanAssign16Bytes()
    {
        var block = new Block();
        Assert.Null(block.HeaderChecksum);
        block.HeaderChecksum = new byte[16];
        Assert.Equal(16, block.HeaderChecksum.Length);
    }

    [Fact]
    public void PayloadChecksum_DefaultsToNull_CanAssign16Bytes()
    {
        var block = new Block();
        Assert.Null(block.PayloadChecksum);
        block.PayloadChecksum = new byte[16];
        Assert.Equal(16, block.PayloadChecksum.Length);
    }
}
