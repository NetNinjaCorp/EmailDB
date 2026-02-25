using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class ShouldEncryptPerPolicyTests
{
    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.Cleanup)]
    [InlineData(BlockType.BTreeLeaf)]
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    [InlineData(BlockType.EmailContent)]
    public void ShouldEncrypt_EmptyPolicy_ReturnsFalseForAll(BlockType blockType)
    {
        var policy = new EncryptionPolicy(Array.Empty<BlockType>());
        Assert.False(policy.ShouldEncrypt(blockType));
    }

    [Fact]
    public void ShouldEncrypt_SingleType_ReturnsTrueOnlyForThatType()
    {
        var policy = new EncryptionPolicy(new[] { BlockType.WAL });

        Assert.True(policy.ShouldEncrypt(BlockType.WAL));

        foreach (var bt in Enum.GetValues<BlockType>().Where(bt => bt != BlockType.WAL))
        {
            Assert.False(policy.ShouldEncrypt(bt));
        }
    }

    [Fact]
    public void ShouldEncrypt_AllTypes_ReturnsTrueForAll()
    {
        var policy = new EncryptionPolicy(Enum.GetValues<BlockType>());

        foreach (var bt in Enum.GetValues<BlockType>())
        {
            Assert.True(policy.ShouldEncrypt(bt));
        }
    }

    [Fact]
    public void ShouldEncrypt_DefaultAndFull_AgreeOnEmailContent()
    {
        Assert.True(EncryptionPolicy.Default.ShouldEncrypt(BlockType.EmailContent));
        Assert.True(EncryptionPolicy.Full.ShouldEncrypt(BlockType.EmailContent));
    }

    [Fact]
    public void ShouldEncrypt_DefaultAndFull_DifferOnCleanup()
    {
        Assert.False(EncryptionPolicy.Default.ShouldEncrypt(BlockType.Cleanup));
        Assert.True(EncryptionPolicy.Full.ShouldEncrypt(BlockType.Cleanup));
    }

    [Fact]
    public void ShouldEncrypt_DefaultAndFull_BothExcludeMetadata()
    {
        Assert.False(EncryptionPolicy.Default.ShouldEncrypt(BlockType.Metadata));
        Assert.False(EncryptionPolicy.Full.ShouldEncrypt(BlockType.Metadata));
    }

    [Fact]
    public void ShouldEncrypt_DuplicateTypesInConstructor_StillReturnsCorrectly()
    {
        var policy = new EncryptionPolicy(new[]
        {
            BlockType.WAL, BlockType.WAL, BlockType.Folder, BlockType.Folder
        });

        Assert.True(policy.ShouldEncrypt(BlockType.WAL));
        Assert.True(policy.ShouldEncrypt(BlockType.Folder));
        Assert.False(policy.ShouldEncrypt(BlockType.Metadata));
    }

    [Fact]
    public void ShouldEncrypt_ReturnsConsistentResultsOnRepeatedCalls()
    {
        var policy = EncryptionPolicy.Default;

        for (int i = 0; i < 100; i++)
        {
            Assert.True(policy.ShouldEncrypt(BlockType.EmailContent));
            Assert.False(policy.ShouldEncrypt(BlockType.Metadata));
        }
    }
}
