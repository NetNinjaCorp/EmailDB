using EmailDB.Format;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

public class BTreeLeafNodeTests
{
    [Fact]
    public void MaxEntries_Is82()
    {
        Assert.Equal(82, BTreeLeafNode.MaxEntries);
    }

    [Fact]
    public void LeafEntry_Size_Is48()
    {
        Assert.Equal(48, LeafEntry.Size);
    }

    [Fact]
    public void LeafNode_HeaderSize_Is69()
    {
        Assert.Equal(69, BTreeLeafNode.HeaderSize);
    }

    [Fact]
    public void SerializeDeserialize_SingleEntry_RoundTrips()
    {
        var key = new EmailHashedID(1, 2, 3, 4);
        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = 1,
            NodeContentHash = CreateHash(0xAA),
            PrevChainHash = CreateHash(0xBB),
            Entries = new[]
            {
                new LeafEntry { Key = key, BlockOffset = 4096, BlockId = 100 }
            }
        };

        var bytes = BTreeNodeSerializer.SerializeLeaf(node);
        var result = BTreeNodeSerializer.DeserializeLeaf(bytes);

        Assert.Equal(node.NodeType, result.NodeType);
        Assert.Equal(node.Version, result.Version);
        Assert.Equal(node.EntryCount, result.EntryCount);
        Assert.Equal(node.NodeContentHash, result.NodeContentHash);
        Assert.Equal(node.PrevChainHash, result.PrevChainHash);
        Assert.Single(result.Entries);
        Assert.Equal(key, result.Entries[0].Key);
        Assert.Equal(4096L, result.Entries[0].BlockOffset);
        Assert.Equal(100L, result.Entries[0].BlockId);
    }

    [Fact]
    public void SerializeDeserialize_82Entries_RoundTrips()
    {
        const int entryCount = 82;
        var entries = new LeafEntry[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            entries[i] = new LeafEntry
            {
                Key = new EmailHashedID(
                    (ulong)i * 4 + 1,
                    (ulong)i * 4 + 2,
                    (ulong)i * 4 + 3,
                    (ulong)i * 4 + 4),
                BlockOffset = (long)(i + 1) * 4096,
                BlockId = i + 1000
            };
        }

        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)entryCount,
            NodeContentHash = CreateHash(0xCC),
            PrevChainHash = CreateHash(0xDD),
            Entries = entries
        };

        var bytes = BTreeNodeSerializer.SerializeLeaf(node);
        var result = BTreeNodeSerializer.DeserializeLeaf(bytes);

        // Verify header
        Assert.Equal(node.NodeType, result.NodeType);
        Assert.Equal(node.Version, result.Version);
        Assert.Equal(node.EntryCount, result.EntryCount);
        Assert.Equal(node.NodeContentHash, result.NodeContentHash);
        Assert.Equal(node.PrevChainHash, result.PrevChainHash);

        // Verify all entries round-trip correctly
        Assert.Equal(entryCount, result.Entries.Length);
        for (int i = 0; i < entryCount; i++)
        {
            Assert.Equal(entries[i].Key, result.Entries[i].Key);
            Assert.Equal(entries[i].BlockOffset, result.Entries[i].BlockOffset);
            Assert.Equal(entries[i].BlockId, result.Entries[i].BlockId);
        }
    }

    [Fact]
    public void Serialize_82Entries_FitsIn4036Bytes()
    {
        const int entryCount = 82;
        var entries = new LeafEntry[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            entries[i] = new LeafEntry
            {
                Key = new EmailHashedID((ulong)i, (ulong)i, (ulong)i, (ulong)i),
                BlockOffset = i * 4096L,
                BlockId = i
            };
        }

        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)entryCount,
            NodeContentHash = CreateHash(0x00),
            PrevChainHash = CreateHash(0x00),
            Entries = entries
        };

        var bytes = BTreeNodeSerializer.SerializeLeaf(node);

        // 69 header + 82 * 48 entries = 4005 bytes
        Assert.Equal(BTreeLeafNode.HeaderSize + entryCount * LeafEntry.Size, bytes.Length);
        Assert.True(bytes.Length <= BTreeLeafNode.MaxPayload,
            $"Serialized leaf with {entryCount} entries is {bytes.Length} bytes, exceeds {BTreeLeafNode.MaxPayload}");
    }

    [Fact]
    public void Serialize_83Entries_ExceedsMaxAndThrows()
    {
        const int entryCount = 83;
        var entries = new LeafEntry[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            entries[i] = new LeafEntry
            {
                Key = new EmailHashedID((ulong)i, (ulong)i, (ulong)i, (ulong)i),
                BlockOffset = i * 4096L,
                BlockId = i
            };
        }

        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)entryCount,
            NodeContentHash = CreateHash(0x00),
            PrevChainHash = CreateHash(0x00),
            Entries = entries
        };

        Assert.Throws<ArgumentException>(() => BTreeNodeSerializer.SerializeLeaf(node));
    }

    [Fact]
    public void SerializeDeserialize_EmptyNode_RoundTrips()
    {
        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = 0,
            NodeContentHash = CreateHash(0x00),
            PrevChainHash = CreateHash(0x00),
            Entries = Array.Empty<LeafEntry>()
        };

        var bytes = BTreeNodeSerializer.SerializeLeaf(node);
        var result = BTreeNodeSerializer.DeserializeLeaf(bytes);

        Assert.Equal(0, result.EntryCount);
        Assert.Empty(result.Entries);
        Assert.Equal(BTreeLeafNode.HeaderSize, bytes.Length);
    }

    [Fact]
    public void SerializeDeserialize_PreservesHashBoundaryValues()
    {
        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = 2,
            NodeContentHash = CreateHash(0xFF),
            PrevChainHash = CreateHash(0x00),
            Entries = new[]
            {
                new LeafEntry
                {
                    Key = new EmailHashedID(ulong.MinValue, ulong.MinValue, ulong.MinValue, ulong.MinValue),
                    BlockOffset = long.MinValue,
                    BlockId = long.MinValue
                },
                new LeafEntry
                {
                    Key = new EmailHashedID(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue),
                    BlockOffset = long.MaxValue,
                    BlockId = long.MaxValue
                }
            }
        };

        var bytes = BTreeNodeSerializer.SerializeLeaf(node);
        var result = BTreeNodeSerializer.DeserializeLeaf(bytes);

        Assert.Equal(ulong.MinValue, result.Entries[0].Key.Part1);
        Assert.Equal(long.MinValue, result.Entries[0].BlockOffset);
        Assert.Equal(ulong.MaxValue, result.Entries[1].Key.Part1);
        Assert.Equal(long.MaxValue, result.Entries[1].BlockOffset);
    }

    private static byte[] CreateHash(byte fill)
    {
        var hash = new byte[32];
        Array.Fill(hash, fill);
        return hash;
    }
}
