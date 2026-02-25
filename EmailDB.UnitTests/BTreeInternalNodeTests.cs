using EmailDB.Format;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeInternalNodeTests
{
    [Fact]
    public void MaxKeys_Is54()
    {
        Assert.Equal(54, BTreeInternalNode.MaxKeys);
    }

    [Fact]
    public void MaxChildren_Is55()
    {
        Assert.Equal(55, BTreeInternalNode.MaxChildren);
    }

    [Fact]
    public void InternalNode_HeaderSize_Is69()
    {
        Assert.Equal(69, BTreeInternalNode.HeaderSize);
    }

    [Fact]
    public void SerializeDeserialize_SingleKey_RoundTrips()
    {
        var key = new EmailHashedID(10, 20, 30, 40);
        var node = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = 1,
            NodeContentHash = CreateHash(0xAA),
            PrevChainHash = CreateHash(0xBB),
            Keys = new[] { key },
            ChildOffsets = new long[] { 4096, 8192 },
            ChildHashes = new[] { CreateHash(0x01), CreateHash(0x02) }
        };

        var bytes = BTreeNodeSerializer.SerializeInternal(node);
        var result = BTreeNodeSerializer.DeserializeInternal(bytes);

        Assert.Equal(node.NodeType, result.NodeType);
        Assert.Equal(node.Version, result.Version);
        Assert.Equal(node.KeyCount, result.KeyCount);
        Assert.Equal(node.NodeContentHash, result.NodeContentHash);
        Assert.Equal(node.PrevChainHash, result.PrevChainHash);
        Assert.Single(result.Keys);
        Assert.Equal(key, result.Keys[0]);
        Assert.Equal(2, result.ChildOffsets.Length);
        Assert.Equal(4096L, result.ChildOffsets[0]);
        Assert.Equal(8192L, result.ChildOffsets[1]);
        Assert.Equal(2, result.ChildHashes.Length);
        Assert.Equal(node.ChildHashes[0], result.ChildHashes[0]);
        Assert.Equal(node.ChildHashes[1], result.ChildHashes[1]);
    }

    [Fact]
    public void SerializeDeserialize_54Keys55Children_RoundTrips()
    {
        const int keyCount = 54;
        const int childCount = 55;

        var keys = new EmailHashedID[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            keys[i] = new EmailHashedID(
                (ulong)i * 4 + 1,
                (ulong)i * 4 + 2,
                (ulong)i * 4 + 3,
                (ulong)i * 4 + 4);
        }

        var childOffsets = new long[childCount];
        var childHashes = new byte[childCount][];
        for (int i = 0; i < childCount; i++)
        {
            childOffsets[i] = (long)(i + 1) * 4096;
            childHashes[i] = CreateHash((byte)(i & 0xFF));
        }

        var node = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)keyCount,
            NodeContentHash = CreateHash(0xCC),
            PrevChainHash = CreateHash(0xDD),
            Keys = keys,
            ChildOffsets = childOffsets,
            ChildHashes = childHashes
        };

        var bytes = BTreeNodeSerializer.SerializeInternal(node);
        var result = BTreeNodeSerializer.DeserializeInternal(bytes);

        // Verify header
        Assert.Equal(node.NodeType, result.NodeType);
        Assert.Equal(node.Version, result.Version);
        Assert.Equal(node.KeyCount, result.KeyCount);
        Assert.Equal(node.NodeContentHash, result.NodeContentHash);
        Assert.Equal(node.PrevChainHash, result.PrevChainHash);

        // Verify all keys round-trip
        Assert.Equal(keyCount, result.Keys.Length);
        for (int i = 0; i < keyCount; i++)
        {
            Assert.Equal(keys[i], result.Keys[i]);
        }

        // Verify all child offsets round-trip
        Assert.Equal(childCount, result.ChildOffsets.Length);
        for (int i = 0; i < childCount; i++)
        {
            Assert.Equal(childOffsets[i], result.ChildOffsets[i]);
        }

        // Verify all child hashes round-trip
        Assert.Equal(childCount, result.ChildHashes.Length);
        for (int i = 0; i < childCount; i++)
        {
            Assert.Equal(childHashes[i], result.ChildHashes[i]);
        }
    }

    [Fact]
    public void Serialize_54Keys55Children_FitsIn4036Bytes()
    {
        const int keyCount = 54;
        const int childCount = 55;

        var keys = new EmailHashedID[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            keys[i] = new EmailHashedID((ulong)i, (ulong)i, (ulong)i, (ulong)i);
        }

        var childOffsets = new long[childCount];
        var childHashes = new byte[childCount][];
        for (int i = 0; i < childCount; i++)
        {
            childOffsets[i] = i * 4096L;
            childHashes[i] = CreateHash(0x00);
        }

        var node = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)keyCount,
            NodeContentHash = CreateHash(0x00),
            PrevChainHash = CreateHash(0x00),
            Keys = keys,
            ChildOffsets = childOffsets,
            ChildHashes = childHashes
        };

        var bytes = BTreeNodeSerializer.SerializeInternal(node);

        // 69 header + 54×32 keys + 55×8 offsets + 55×32 hashes
        // = 69 + 1728 + 440 + 1760 = 3997 bytes
        int expectedSize = BTreeInternalNode.HeaderSize
            + keyCount * BTreeInternalNode.KeySize
            + childCount * BTreeInternalNode.OffsetSize
            + childCount * BTreeInternalNode.HashSize;
        Assert.Equal(expectedSize, bytes.Length);
        Assert.True(bytes.Length <= BTreeInternalNode.MaxPayload,
            $"Serialized internal node with {keyCount} keys is {bytes.Length} bytes, exceeds {BTreeInternalNode.MaxPayload}");
    }

    [Fact]
    public void Serialize_55Keys_ExceedsMaxAndThrows()
    {
        const int keyCount = 55;
        const int childCount = 56;

        var keys = new EmailHashedID[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            keys[i] = new EmailHashedID((ulong)i, (ulong)i, (ulong)i, (ulong)i);
        }

        var childOffsets = new long[childCount];
        var childHashes = new byte[childCount][];
        for (int i = 0; i < childCount; i++)
        {
            childOffsets[i] = i * 4096L;
            childHashes[i] = CreateHash(0x00);
        }

        var node = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)keyCount,
            NodeContentHash = CreateHash(0x00),
            PrevChainHash = CreateHash(0x00),
            Keys = keys,
            ChildOffsets = childOffsets,
            ChildHashes = childHashes
        };

        Assert.Throws<ArgumentException>(() => BTreeNodeSerializer.SerializeInternal(node));
    }

    [Fact]
    public void SerializeDeserialize_EmptyInternalNode_RoundTrips()
    {
        var node = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = 0,
            NodeContentHash = CreateHash(0x00),
            PrevChainHash = CreateHash(0x00),
            Keys = Array.Empty<EmailHashedID>(),
            ChildOffsets = new long[] { 4096 },
            ChildHashes = new[] { CreateHash(0xFF) }
        };

        var bytes = BTreeNodeSerializer.SerializeInternal(node);
        var result = BTreeNodeSerializer.DeserializeInternal(bytes);

        Assert.Equal(0, result.KeyCount);
        Assert.Empty(result.Keys);
        Assert.Single(result.ChildOffsets);
        Assert.Equal(4096L, result.ChildOffsets[0]);
        Assert.Single(result.ChildHashes);
        Assert.Equal(node.ChildHashes[0], result.ChildHashes[0]);
    }

    [Fact]
    public void SerializeDeserialize_PreservesBoundaryValues()
    {
        var node = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = 1,
            NodeContentHash = CreateHash(0xFF),
            PrevChainHash = CreateHash(0x00),
            Keys = new[]
            {
                new EmailHashedID(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue)
            },
            ChildOffsets = new[] { long.MinValue, long.MaxValue },
            ChildHashes = new[] { CreateHash(0x00), CreateHash(0xFF) }
        };

        var bytes = BTreeNodeSerializer.SerializeInternal(node);
        var result = BTreeNodeSerializer.DeserializeInternal(bytes);

        Assert.Equal(ulong.MaxValue, result.Keys[0].Part1);
        Assert.Equal(ulong.MaxValue, result.Keys[0].Part4);
        Assert.Equal(long.MinValue, result.ChildOffsets[0]);
        Assert.Equal(long.MaxValue, result.ChildOffsets[1]);
        Assert.Equal(node.ChildHashes[0], result.ChildHashes[0]);
        Assert.Equal(node.ChildHashes[1], result.ChildHashes[1]);
    }

    private static byte[] CreateHash(byte fill)
    {
        var hash = new byte[32];
        Array.Fill(hash, fill);
        return hash;
    }
}
