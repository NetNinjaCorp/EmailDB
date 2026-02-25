using EmailDB.Format;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class IndexRootTests
{
    [Fact]
    public void PayloadSize_Is90()
    {
        Assert.Equal(90, IndexRoot.PayloadSize);
    }

    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var root = new IndexRoot
        {
            RootNodeBlockOffset = 8192,
            EntryCount = 50_000,
            TreeHeight = 3,
            RootNodeHash = CreateHash(0xAA),
            PreviousRootHash = CreateHash(0xBB),
            PreviousRootOffset = 4096
        };

        var bytes = BTreeNodeSerializer.SerializeIndexRoot(root);
        var result = BTreeNodeSerializer.DeserializeIndexRoot(bytes);

        Assert.Equal(root.RootNodeBlockOffset, result.RootNodeBlockOffset);
        Assert.Equal(root.EntryCount, result.EntryCount);
        Assert.Equal(root.TreeHeight, result.TreeHeight);
        Assert.Equal(root.RootNodeHash, result.RootNodeHash);
        Assert.Equal(root.PreviousRootHash, result.PreviousRootHash);
        Assert.Equal(root.PreviousRootOffset, result.PreviousRootOffset);
    }

    [Fact]
    public void Serialize_ProducesExactPayloadSize()
    {
        var root = new IndexRoot
        {
            RootNodeBlockOffset = 0,
            EntryCount = 0,
            TreeHeight = 0,
            RootNodeHash = new byte[32],
            PreviousRootHash = new byte[32],
            PreviousRootOffset = 0
        };

        var bytes = BTreeNodeSerializer.SerializeIndexRoot(root);

        Assert.Equal(IndexRoot.PayloadSize, bytes.Length);
    }

    [Fact]
    public void SerializeDeserialize_BoundaryValues_RoundTrips()
    {
        var root = new IndexRoot
        {
            RootNodeBlockOffset = long.MaxValue,
            EntryCount = long.MaxValue,
            TreeHeight = ushort.MaxValue,
            RootNodeHash = CreateHash(0xFF),
            PreviousRootHash = CreateHash(0x00),
            PreviousRootOffset = long.MinValue
        };

        var bytes = BTreeNodeSerializer.SerializeIndexRoot(root);
        var result = BTreeNodeSerializer.DeserializeIndexRoot(bytes);

        Assert.Equal(long.MaxValue, result.RootNodeBlockOffset);
        Assert.Equal(long.MaxValue, result.EntryCount);
        Assert.Equal(ushort.MaxValue, result.TreeHeight);
        Assert.Equal(root.RootNodeHash, result.RootNodeHash);
        Assert.Equal(root.PreviousRootHash, result.PreviousRootHash);
        Assert.Equal(long.MinValue, result.PreviousRootOffset);
    }

    [Fact]
    public void SerializeDeserialize_FirstRoot_NoPreviousHash()
    {
        var root = new IndexRoot
        {
            RootNodeBlockOffset = 4096,
            EntryCount = 1,
            TreeHeight = 1,
            RootNodeHash = CreateHash(0xCC),
            PreviousRootHash = new byte[32], // zero-filled for first root
            PreviousRootOffset = -1          // no previous root
        };

        var bytes = BTreeNodeSerializer.SerializeIndexRoot(root);
        var result = BTreeNodeSerializer.DeserializeIndexRoot(bytes);

        Assert.Equal(4096L, result.RootNodeBlockOffset);
        Assert.Equal(1L, result.EntryCount);
        Assert.Equal((ushort)1, result.TreeHeight);
        Assert.Equal(root.RootNodeHash, result.RootNodeHash);
        Assert.All(result.PreviousRootHash, b => Assert.Equal(0, b));
        Assert.Equal(-1L, result.PreviousRootOffset);
    }

    [Fact]
    public void Payload_FitsWithinBlockPayload()
    {
        const int maxPayload = 4036; // 4096 - 60 byte block overhead
        Assert.True(IndexRoot.PayloadSize <= maxPayload,
            $"IndexRoot payload ({IndexRoot.PayloadSize} bytes) exceeds max block payload ({maxPayload} bytes)");
    }

    private static byte[] CreateHash(byte fill)
    {
        var hash = new byte[32];
        Array.Fill(hash, fill);
        return hash;
    }
}
