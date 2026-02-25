using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class CustomEncryptionPolicyTests
{
    [Fact]
    public void Custom_EncryptsOnlySpecifiedTypes()
    {
        var policy = new EncryptionPolicy(new[] { BlockType.BTreeLeaf, BlockType.Cleanup });

        Assert.True(policy.ShouldEncrypt(BlockType.BTreeLeaf));
        Assert.True(policy.ShouldEncrypt(BlockType.Cleanup));
        Assert.False(policy.ShouldEncrypt(BlockType.EmailContent));
        Assert.False(policy.ShouldEncrypt(BlockType.Metadata));
        Assert.False(policy.ShouldEncrypt(BlockType.WAL));
    }

    [Fact]
    public void Custom_EmptyPolicy_EncryptsNothing()
    {
        var policy = new EncryptionPolicy(Array.Empty<BlockType>());

        foreach (var bt in Enum.GetValues<BlockType>())
        {
            Assert.False(policy.ShouldEncrypt(bt));
        }
    }

    [Fact]
    public void Custom_MetadataOnly_CanEncryptMetadata()
    {
        var policy = new EncryptionPolicy(new[] { BlockType.Metadata });

        Assert.True(policy.ShouldEncrypt(BlockType.Metadata));
        Assert.False(policy.ShouldEncrypt(BlockType.EmailContent));
    }

    [Fact]
    public void Custom_AllTypes_EncryptsEverything()
    {
        var policy = new EncryptionPolicy(Enum.GetValues<BlockType>());

        foreach (var bt in Enum.GetValues<BlockType>())
        {
            Assert.True(policy.ShouldEncrypt(bt));
        }
    }

    [Fact]
    public void Custom_SubsetDifferentFromDefaultAndFull()
    {
        var customTypes = new[] { BlockType.Metadata, BlockType.IndexRoot, BlockType.BTreeInternal };
        var policy = new EncryptionPolicy(customTypes);

        // Metadata is excluded by both Default and Full, but our custom policy includes it
        Assert.True(policy.ShouldEncrypt(BlockType.Metadata));
        Assert.True(policy.ShouldEncrypt(BlockType.IndexRoot));
        Assert.True(policy.ShouldEncrypt(BlockType.BTreeInternal));

        // EmailContent is included by both Default and Full, but not in our custom policy
        Assert.False(policy.ShouldEncrypt(BlockType.EmailContent));
        Assert.False(policy.ShouldEncrypt(BlockType.WAL));
    }

    [Fact]
    public void Custom_FromList_WorksWithListInput()
    {
        var types = new List<BlockType> { BlockType.Segment, BlockType.Folder };
        var policy = new EncryptionPolicy(types);

        Assert.True(policy.ShouldEncrypt(BlockType.Segment));
        Assert.True(policy.ShouldEncrypt(BlockType.Folder));
        Assert.False(policy.ShouldEncrypt(BlockType.BTreeLeaf));
    }

    [Fact]
    public void Custom_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EncryptionPolicy(null!));
    }

    [Fact]
    public void Custom_TwoIndependentPolicies_DoNotInterfere()
    {
        var policyA = new EncryptionPolicy(new[] { BlockType.WAL });
        var policyB = new EncryptionPolicy(new[] { BlockType.Cleanup });

        Assert.True(policyA.ShouldEncrypt(BlockType.WAL));
        Assert.False(policyA.ShouldEncrypt(BlockType.Cleanup));

        Assert.False(policyB.ShouldEncrypt(BlockType.WAL));
        Assert.True(policyB.ShouldEncrypt(BlockType.Cleanup));
    }
}
