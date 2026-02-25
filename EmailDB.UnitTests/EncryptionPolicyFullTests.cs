using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class EncryptionPolicyFullTests
{
    private readonly EncryptionPolicy _policy = EncryptionPolicy.Full;

    [Theory]
    [InlineData(BlockType.WAL)]
    [InlineData(BlockType.FolderTree)]
    [InlineData(BlockType.Folder)]
    [InlineData(BlockType.Segment)]
    [InlineData(BlockType.Cleanup)]
    [InlineData(BlockType.BTreeLeaf)]
    [InlineData(BlockType.BTreeInternal)]
    [InlineData(BlockType.IndexRoot)]
    [InlineData(BlockType.EmailContent)]
    public void Full_ShouldEncrypt_ReturnsTrue_ForAllNonMetadataTypes(BlockType blockType)
    {
        Assert.True(_policy.ShouldEncrypt(blockType));
    }

    [Fact]
    public void Full_ShouldEncrypt_ReturnsFalse_ForMetadata()
    {
        Assert.False(_policy.ShouldEncrypt(BlockType.Metadata));
    }

    [Fact]
    public void Full_EncryptsAllBlockTypesExceptMetadata()
    {
        var allTypes = Enum.GetValues<BlockType>();
        var encrypted = allTypes.Where(bt => _policy.ShouldEncrypt(bt)).ToList();
        var notEncrypted = allTypes.Where(bt => !_policy.ShouldEncrypt(bt)).ToList();

        Assert.Equal(allTypes.Length - 1, encrypted.Count);
        Assert.Single(notEncrypted);
        Assert.Equal(BlockType.Metadata, notEncrypted[0]);
    }

    [Fact]
    public void Full_IsSingletonInstance()
    {
        var a = EncryptionPolicy.Full;
        var b = EncryptionPolicy.Full;
        Assert.Same(a, b);
    }
}
