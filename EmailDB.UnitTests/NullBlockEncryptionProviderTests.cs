using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class NullBlockEncryptionProviderTests
{
    private readonly NullBlockEncryptionProvider _provider = NullBlockEncryptionProvider.Instance;

    [Fact]
    public void Encrypt_ReturnsSameBytesAsInput()
    {
        byte[] payload = { 0x01, 0x02, 0x03, 0xFF, 0x00, 0xAB };
        var result = _provider.Encrypt(payload, BlockType.EmailContent, blockId: 1);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void Decrypt_ReturnsSameBytesAsInput()
    {
        byte[] payload = { 0x01, 0x02, 0x03, 0xFF, 0x00, 0xAB };
        var result = _provider.Decrypt(payload, BlockType.EmailContent, blockId: 1);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void Encrypt_EmptyPayload_ReturnsEmptyArray()
    {
        var result = _provider.Encrypt(ReadOnlySpan<byte>.Empty, BlockType.Metadata, blockId: 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Decrypt_EmptyPayload_ReturnsEmptyArray()
    {
        var result = _provider.Decrypt(ReadOnlySpan<byte>.Empty, BlockType.Metadata, blockId: 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Encrypt_ReturnsNewArrayInstance_NotSameReference()
    {
        byte[] payload = { 0x01, 0x02 };
        var result = _provider.Encrypt(payload, BlockType.Segment, blockId: 5);
        Assert.Equal(payload, result);
        Assert.NotSame(payload, result);
    }

    [Fact]
    public void Decrypt_ReturnsNewArrayInstance_NotSameReference()
    {
        byte[] payload = { 0x01, 0x02 };
        var result = _provider.Decrypt(payload, BlockType.Segment, blockId: 5);
        Assert.Equal(payload, result);
        Assert.NotSame(payload, result);
    }

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
    public void ShouldEncrypt_ReturnsFalse_ForAllBlockTypes(BlockType blockType)
    {
        Assert.False(_provider.ShouldEncrypt(blockType));
    }

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
    public void Encrypt_PassesThroughUnchanged_ForAllBlockTypes(BlockType blockType)
    {
        byte[] payload = { 0xDE, 0xAD, 0xBE, 0xEF };
        var result = _provider.Encrypt(payload, blockType, blockId: 42);
        Assert.Equal(payload, result);
    }

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
    public void Decrypt_PassesThroughUnchanged_ForAllBlockTypes(BlockType blockType)
    {
        byte[] payload = { 0xDE, 0xAD, 0xBE, 0xEF };
        var result = _provider.Decrypt(payload, blockType, blockId: 42);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void OverheadBytes_IsZero()
    {
        Assert.Equal(0, _provider.OverheadBytes);
    }

    [Fact]
    public void IsEnabled_IsFalse()
    {
        Assert.False(_provider.IsEnabled);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var provider = NullBlockEncryptionProvider.Instance;
        var ex = Record.Exception(() => provider.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        var a = NullBlockEncryptionProvider.Instance;
        var b = NullBlockEncryptionProvider.Instance;
        Assert.Same(a, b);
    }

    [Fact]
    public void Encrypt_LargePayload_PassesThroughUnchanged()
    {
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        var result = _provider.Encrypt(payload, BlockType.EmailContent, blockId: 999);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void Decrypt_LargePayload_PassesThroughUnchanged()
    {
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        var result = _provider.Decrypt(payload, BlockType.EmailContent, blockId: 999);
        Assert.Equal(payload, result);
    }

    [Fact]
    public void RoundTrip_EncryptThenDecrypt_ReturnsSamePayload()
    {
        byte[] original = { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var encrypted = _provider.Encrypt(original, BlockType.EmailContent, blockId: 10);
        var decrypted = _provider.Decrypt(encrypted, BlockType.EmailContent, blockId: 10);
        Assert.Equal(original, decrypted);
    }
}
