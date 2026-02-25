using EmailDB.Format.Helpers;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that BLAKE3 hashing produces correct 32-byte digests for B+-tree node content.
/// Covers: leaf nodes, internal nodes, raw data, determinism, and collision resistance.
/// </summary>
public class Blake3HashingTests
{
    [Fact]
    public void ComputeHash_ReturnsExactly32Bytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var hash = BTreeHasher.ComputeHash(data);

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputeHash_EmptyInput_Returns32Bytes()
    {
        var hash = BTreeHasher.ComputeHash(ReadOnlySpan<byte>.Empty);

        Assert.Equal(32, hash.Length);
        // BLAKE3 hash of empty input is a well-known constant
        Assert.NotEqual(new byte[32], hash);
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var hash1 = BTreeHasher.ComputeHash(data);
        var hash2 = BTreeHasher.ComputeHash(data);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_DifferentInputs_ProduceDifferentDigests()
    {
        var hash1 = BTreeHasher.ComputeHash(new byte[] { 0x00 });
        var hash2 = BTreeHasher.ComputeHash(new byte[] { 0x01 });

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_SingleBitChange_ProducesDifferentDigest()
    {
        var data1 = new byte[64];
        var data2 = new byte[64];
        data2[63] = 0x01; // flip one bit in the last byte

        var hash1 = BTreeHasher.ComputeHash(data1);
        var hash2 = BTreeHasher.ComputeHash(data2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeLeafContentHash_Returns32Bytes()
    {
        var node = CreateLeafNode(5);
        var hash = BTreeHasher.ComputeLeafContentHash(node);

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputeLeafContentHash_IsDeterministic()
    {
        var node = CreateLeafNode(10);

        var hash1 = BTreeHasher.ComputeLeafContentHash(node);
        var hash2 = BTreeHasher.ComputeLeafContentHash(node);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeLeafContentHash_DifferentEntries_ProduceDifferentDigests()
    {
        var node1 = CreateLeafNode(5);
        var node2 = CreateLeafNode(5);
        // Modify one entry in node2
        node2.Entries[0] = new LeafEntry
        {
            Key = new EmailHashedID(999, 999, 999, 999),
            BlockOffset = 999,
            BlockId = 999
        };

        var hash1 = BTreeHasher.ComputeLeafContentHash(node1);
        var hash2 = BTreeHasher.ComputeLeafContentHash(node2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeLeafContentHash_IgnoresHeaderFields()
    {
        // Two leaf nodes with identical entries but different header metadata
        var node1 = CreateLeafNode(3);
        node1.Version = 1;
        node1.NodeContentHash = CreateFillHash(0xAA);
        node1.PrevChainHash = CreateFillHash(0xBB);

        var node2 = CreateLeafNode(3);
        node2.Version = 99;
        node2.NodeContentHash = CreateFillHash(0xCC);
        node2.PrevChainHash = CreateFillHash(0xDD);

        var hash1 = BTreeHasher.ComputeLeafContentHash(node1);
        var hash2 = BTreeHasher.ComputeLeafContentHash(node2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeLeafContentHash_EmptyNode_Returns32Bytes()
    {
        var node = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = 0,
            Entries = Array.Empty<LeafEntry>()
        };

        var hash = BTreeHasher.ComputeLeafContentHash(node);

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputeLeafContentHash_MaxEntries_Returns32Bytes()
    {
        var node = CreateLeafNode(BTreeLeafNode.MaxEntries);
        var hash = BTreeHasher.ComputeLeafContentHash(node);

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputeInternalContentHash_Returns32Bytes()
    {
        var node = CreateInternalNode(5);
        var hash = BTreeHasher.ComputeInternalContentHash(node);

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void ComputeInternalContentHash_IsDeterministic()
    {
        var node = CreateInternalNode(10);

        var hash1 = BTreeHasher.ComputeInternalContentHash(node);
        var hash2 = BTreeHasher.ComputeInternalContentHash(node);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeInternalContentHash_DifferentKeys_ProduceDifferentDigests()
    {
        var node1 = CreateInternalNode(5);
        var node2 = CreateInternalNode(5);
        // Modify one key in node2
        node2.Keys[0] = new EmailHashedID(999, 999, 999, 999);

        var hash1 = BTreeHasher.ComputeInternalContentHash(node1);
        var hash2 = BTreeHasher.ComputeInternalContentHash(node2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeInternalContentHash_DifferentChildHashes_ProduceDifferentDigests()
    {
        var node1 = CreateInternalNode(3);
        var node2 = CreateInternalNode(3);
        // Modify one child hash in node2
        node2.ChildHashes[0] = CreateFillHash(0xFF);

        var hash1 = BTreeHasher.ComputeInternalContentHash(node1);
        var hash2 = BTreeHasher.ComputeInternalContentHash(node2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeInternalContentHash_IgnoresHeaderFields()
    {
        var node1 = CreateInternalNode(3);
        node1.Version = 1;
        node1.NodeContentHash = CreateFillHash(0xAA);
        node1.PrevChainHash = CreateFillHash(0xBB);

        var node2 = CreateInternalNode(3);
        node2.Version = 99;
        node2.NodeContentHash = CreateFillHash(0xCC);
        node2.PrevChainHash = CreateFillHash(0xDD);

        var hash1 = BTreeHasher.ComputeInternalContentHash(node1);
        var hash2 = BTreeHasher.ComputeInternalContentHash(node2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeInternalContentHash_MaxKeys_Returns32Bytes()
    {
        var node = CreateInternalNode(BTreeInternalNode.MaxKeys);
        var hash = BTreeHasher.ComputeInternalContentHash(node);

        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void LeafAndInternalHashes_AreDifferent_ForSameKeyData()
    {
        // Even if a leaf and internal node share the same key values,
        // their hashes must differ because the data layout differs
        var key = new EmailHashedID(1, 2, 3, 4);

        var leaf = new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = 1,
            Entries = new[]
            {
                new LeafEntry { Key = key, BlockOffset = 4096, BlockId = 1 }
            }
        };

        var intern = new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = 1,
            Keys = new[] { key },
            ChildOffsets = new long[] { 4096, 8192 },
            ChildHashes = new[] { CreateFillHash(0x00), CreateFillHash(0x00) }
        };

        var leafHash = BTreeHasher.ComputeLeafContentHash(leaf);
        var internalHash = BTreeHasher.ComputeInternalContentHash(intern);

        Assert.NotEqual(leafHash, internalHash);
    }

    [Fact]
    public void ContentHash_IntegratesWithSerializer_RoundTrip()
    {
        // Create a leaf node, compute its content hash, serialize, deserialize,
        // and verify the hash still matches
        var node = CreateLeafNode(10);
        var contentHash = BTreeHasher.ComputeLeafContentHash(node);
        node.NodeContentHash = contentHash;

        var bytes = EmailDB.Format.BTreeNodeSerializer.SerializeLeaf(node);
        var restored = EmailDB.Format.BTreeNodeSerializer.DeserializeLeaf(bytes);

        // Recompute hash from restored node
        var restoredHash = BTreeHasher.ComputeLeafContentHash(restored);

        Assert.Equal(contentHash, restoredHash);
        Assert.Equal(contentHash, restored.NodeContentHash);
    }

    [Fact]
    public void ContentHash_IntegratesWithSerializer_InternalNode_RoundTrip()
    {
        var node = CreateInternalNode(5);
        var contentHash = BTreeHasher.ComputeInternalContentHash(node);
        node.NodeContentHash = contentHash;

        var bytes = EmailDB.Format.BTreeNodeSerializer.SerializeInternal(node);
        var restored = EmailDB.Format.BTreeNodeSerializer.DeserializeInternal(bytes);

        var restoredHash = BTreeHasher.ComputeInternalContentHash(restored);

        Assert.Equal(contentHash, restoredHash);
        Assert.Equal(contentHash, restored.NodeContentHash);
    }

    // --- Helpers ---

    private static BTreeLeafNode CreateLeafNode(int entryCount)
    {
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

        return new BTreeLeafNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeLeaf,
            Version = 1,
            EntryCount = (ushort)entryCount,
            NodeContentHash = new byte[32],
            PrevChainHash = new byte[32],
            Entries = entries
        };
    }

    private static BTreeInternalNode CreateInternalNode(int keyCount)
    {
        var keys = new EmailHashedID[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            keys[i] = new EmailHashedID(
                (ulong)i * 4 + 1,
                (ulong)i * 4 + 2,
                (ulong)i * 4 + 3,
                (ulong)i * 4 + 4);
        }

        int childCount = keyCount + 1;
        var childOffsets = new long[childCount];
        var childHashes = new byte[childCount][];
        for (int i = 0; i < childCount; i++)
        {
            childOffsets[i] = (long)(i + 1) * 4096;
            childHashes[i] = CreateFillHash((byte)(i & 0xFF));
        }

        return new BTreeInternalNode
        {
            NodeType = (byte)EmailDB.Format.Models.BlockType.BTreeInternal,
            Version = 1,
            KeyCount = (ushort)keyCount,
            NodeContentHash = new byte[32],
            PrevChainHash = new byte[32],
            Keys = keys,
            ChildOffsets = childOffsets,
            ChildHashes = childHashes
        };
    }

    private static byte[] CreateFillHash(byte fill)
    {
        var hash = new byte[32];
        Array.Fill(hash, fill);
        return hash;
    }
}
