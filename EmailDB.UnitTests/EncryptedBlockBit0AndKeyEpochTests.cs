using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class EncryptedBlockBit0AndKeyEpochTests
{
    [Fact]
    public void EncryptedBlock_HasBit0Set()
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        Assert.True((block.Flags & 0x01) != 0, "Bit 0 should be set for encrypted blocks");
        Assert.True(block.IsEncrypted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(42)]
    [InlineData(127)]
    public void EncryptedBlock_KeyEpochMatchesActiveEpochAtWriteTime(byte activeEpoch)
    {
        // Simulate write-time: set encrypted flag, then set the active epoch
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(activeEpoch);

        // Bit 0 must still be set after SetKeyEpoch
        Assert.True(block.IsEncrypted, "Bit 0 (encrypted) must remain set after SetKeyEpoch");
        // KeyEpoch must match the active epoch that was written
        Assert.Equal(activeEpoch, block.KeyEpoch);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(127)]
    public void EncryptedBlock_FlagsEncodeBothEncryptedBitAndEpoch(byte activeEpoch)
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(activeEpoch);

        byte expectedFlags = (byte)((activeEpoch << 1) | Block.FlagEncrypted);
        Assert.Equal(expectedFlags, block.Flags);
    }

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public void EncryptedBlock_AnyEncryptableType_HasBit0SetAndCorrectEpoch(BlockType blockType)
    {
        byte activeEpoch = 3;
        var block = new Block
        {
            Type = blockType,
            Flags = Block.FlagEncrypted
        };
        block.SetKeyEpoch(activeEpoch);

        Assert.True(block.IsEncrypted);
        Assert.Equal(activeEpoch, block.KeyEpoch);
    }

    [Fact]
    public void EncryptedBlock_EpochZero_StillHasBit0Set()
    {
        // Even with epoch 0, encrypted blocks must have bit 0 set
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(0);

        Assert.Equal(0x01, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(0, block.KeyEpoch);
    }

    [Fact]
    public void EncryptedBlock_EpochUpdated_StillHasBit0Set()
    {
        // Simulate key rotation: epoch changes but encrypted flag stays
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(1);
        Assert.True(block.IsEncrypted);
        Assert.Equal(1, block.KeyEpoch);

        // Rotate to new epoch
        block.SetKeyEpoch(2);
        Assert.True(block.IsEncrypted);
        Assert.Equal(2, block.KeyEpoch);
    }

    [Fact]
    public void EncryptedBlock_MaxEpoch_HasBit0SetAndEpoch127()
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(127);

        Assert.Equal(0xFF, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(127, block.KeyEpoch);
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x03)]
    [InlineData(5, 0x0B)]
    [InlineData(63, 0x7F)]
    [InlineData(64, 0x81)]
    [InlineData(127, 0xFF)]
    public void EncryptedBlock_WrittenFlags_MatchExpectedBitPattern(byte epoch, byte expectedFlags)
    {
        var block = new Block { Flags = Block.FlagEncrypted };
        block.SetKeyEpoch(epoch);

        Assert.Equal(expectedFlags, block.Flags);
        Assert.True(block.IsEncrypted);
        Assert.Equal(epoch, block.KeyEpoch);
    }
}
