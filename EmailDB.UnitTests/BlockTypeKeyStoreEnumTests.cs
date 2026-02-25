using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class BlockTypeKeyStoreEnumTests
{
    [Fact]
    public void BlockType_HasKeyStore_WithValue10()
    {
        Assert.Equal((byte)10, (byte)BlockType.KeyStore);
    }

    [Fact]
    public void BlockType_KeyStore_IsDefined()
    {
        Assert.True(Enum.IsDefined(typeof(BlockType), (byte)10));
    }

    [Fact]
    public void BlockType_KeyStore_IsParseable()
    {
        var parsed = Enum.Parse<BlockType>("KeyStore");
        Assert.Equal(BlockType.KeyStore, parsed);
    }
}
