using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class BlockTypeEnumTests
{
    [Fact]
    public void BlockType_HasBTreeLeaf_WithValue6()
    {
        Assert.Equal((byte)6, (byte)BlockType.BTreeLeaf);
    }

    [Fact]
    public void BlockType_HasBTreeInternal_WithValue7()
    {
        Assert.Equal((byte)7, (byte)BlockType.BTreeInternal);
    }

    [Fact]
    public void BlockType_HasIndexRoot_WithValue8()
    {
        Assert.Equal((byte)8, (byte)BlockType.IndexRoot);
    }

    [Fact]
    public void BlockType_HasEmailContent_WithValue9()
    {
        Assert.Equal((byte)9, (byte)BlockType.EmailContent);
    }

    [Fact]
    public void BlockType_AllNewValues_AreParseable()
    {
        Assert.True(Enum.IsDefined(typeof(BlockType), (byte)6));
        Assert.True(Enum.IsDefined(typeof(BlockType), (byte)7));
        Assert.True(Enum.IsDefined(typeof(BlockType), (byte)8));
        Assert.True(Enum.IsDefined(typeof(BlockType), (byte)9));
    }

    [Fact]
    public void BlockType_BackingType_IsByte()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(BlockType)));
    }

    [Fact]
    public void BlockType_ExistingValues_Unchanged()
    {
        Assert.Equal((byte)0, (byte)BlockType.Metadata);
        Assert.Equal((byte)1, (byte)BlockType.WAL);
        Assert.Equal((byte)2, (byte)BlockType.FolderTree);
        Assert.Equal((byte)3, (byte)BlockType.Folder);
        Assert.Equal((byte)4, (byte)BlockType.Segment);
        Assert.Equal((byte)5, (byte)BlockType.Cleanup);
    }
}
