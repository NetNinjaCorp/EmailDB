using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class UnencryptedBlockFlagsAndKeyEpochTests
{
    [Fact]
    public void NewBlock_HasFlagsZero()
    {
        var block = new Block();
        Assert.Equal(0, block.Flags);
    }

    [Fact]
    public void NewBlock_HasKeyEpochZero()
    {
        var block = new Block();
        Assert.Equal(0, block.KeyEpoch);
    }

    [Fact]
    public void NewBlock_IsNotEncrypted()
    {
        var block = new Block();
        Assert.False(block.IsEncrypted);
    }

    [Fact]
    public void UnencryptedBlock_WithExplicitFlagsZero_HasKeyEpochZero()
    {
        var block = new Block { Flags = 0x00 };
        Assert.Equal(0, block.Flags);
        Assert.Equal(0, block.KeyEpoch);
        Assert.False(block.IsEncrypted);
    }

    [Fact]
    public void UnencryptedBlock_SetKeyEpochZero_KeepsFlagsAtZero()
    {
        var block = new Block();
        block.SetKeyEpoch(0);
        Assert.Equal(0, block.Flags);
        Assert.Equal(0, block.KeyEpoch);
        Assert.False(block.IsEncrypted);
    }

    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    public void UnencryptedBlock_AnyBlockType_HasFlagsAndKeyEpochZero(BlockType blockType)
    {
        var block = new Block { Type = blockType, Flags = 0x00 };
        Assert.Equal(0, block.Flags);
        Assert.Equal(0, block.KeyEpoch);
        Assert.False(block.IsEncrypted);
    }
}
