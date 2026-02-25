using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class EncryptionPolicyDefaultTests
{
    private readonly EncryptionPolicy _policy = EncryptionPolicy.Default;

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.WAL)]
    public void Default_ShouldEncrypt_ReturnsTrue_ForEncryptedTypes(BlockType blockType)
    {
        Assert.True(_policy.ShouldEncrypt(blockType));
    }

    [Theory]
    [InlineData(BlockType.Metadata)]
    [InlineData(BlockType.Cleanup)]
    [InlineData(BlockType.BTreeLeaf)]
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    public void Default_ShouldEncrypt_ReturnsFalse_ForNonEncryptedTypes(BlockType blockType)
    {
        Assert.False(_policy.ShouldEncrypt(blockType));
    }

    [Fact]
    public void Default_EncryptsExactlyFiveBlockTypes()
    {
        var encryptedTypes = Enum.GetValues<BlockType>()
            .Where(bt => _policy.ShouldEncrypt(bt))
            .ToList();

        Assert.Equal(5, encryptedTypes.Count);
    }

    [Fact]
    public void Default_IsSingletonInstance()
    {
        var a = EncryptionPolicy.Default;
        var b = EncryptionPolicy.Default;
        Assert.Same(a, b);
    }
}
