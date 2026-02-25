using System.Buffers.Binary;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format;

/// <summary>
/// Custom binary serializer for B+-tree nodes using BinaryPrimitives for direct buffer writes.
/// Designed for fixed-size fields to be faster than protobuf.
/// </summary>
public static class BTreeNodeSerializer
{
    public static byte[] SerializeLeaf(BTreeLeafNode node)
    {
        if (node.Entries.Length > BTreeLeafNode.MaxEntries)
            throw new ArgumentException(
                $"Leaf node has {node.Entries.Length} entries, max is {BTreeLeafNode.MaxEntries}.");

        int size = BTreeLeafNode.HeaderSize + node.EntryCount * LeafEntry.Size;
        var buf = new byte[size];
        var span = buf.AsSpan();
        int offset = 0;

        // Header
        span[offset++] = node.NodeType;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), node.Version);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), node.EntryCount);
        offset += 2;
        node.NodeContentHash.AsSpan(0, 32).CopyTo(span.Slice(offset));
        offset += 32;
        node.PrevChainHash.AsSpan(0, 32).CopyTo(span.Slice(offset));
        offset += 32;

        // Entries
        for (int i = 0; i < node.EntryCount; i++)
        {
            ref readonly var entry = ref node.Entries[i];
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), entry.Key.Part1);
            offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), entry.Key.Part2);
            offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), entry.Key.Part3);
            offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), entry.Key.Part4);
            offset += 8;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), entry.BlockOffset);
            offset += 8;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), entry.BlockId);
            offset += 8;
        }

        return buf;
    }

    public static BTreeLeafNode DeserializeLeaf(byte[] data)
    {
        var span = data.AsSpan();
        int offset = 0;

        var node = new BTreeLeafNode();
        node.NodeType = span[offset++];
        node.Version = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
        offset += 2;
        node.EntryCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
        offset += 2;
        node.NodeContentHash = span.Slice(offset, 32).ToArray();
        offset += 32;
        node.PrevChainHash = span.Slice(offset, 32).ToArray();
        offset += 32;

        node.Entries = new LeafEntry[node.EntryCount];
        for (int i = 0; i < node.EntryCount; i++)
        {
            var p1 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset));
            offset += 8;
            var p2 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset));
            offset += 8;
            var p3 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset));
            offset += 8;
            var p4 = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset));
            offset += 8;

            node.Entries[i] = new LeafEntry
            {
                Key = new EmailHashedID(p1, p2, p3, p4),
                BlockOffset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset)),
                BlockId = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 8))
            };
            offset += 16;
        }

        return node;
    }

    public static byte[] SerializeInternal(BTreeInternalNode node)
    {
        if (node.Keys.Length > BTreeInternalNode.MaxKeys)
            throw new ArgumentException(
                $"Internal node has {node.Keys.Length} keys, max is {BTreeInternalNode.MaxKeys}.");

        int childCount = node.KeyCount + 1;
        int size = BTreeInternalNode.HeaderSize
            + node.KeyCount * BTreeInternalNode.KeySize
            + childCount * BTreeInternalNode.OffsetSize
            + childCount * BTreeInternalNode.HashSize;

        var buf = new byte[size];
        var span = buf.AsSpan();
        int offset = 0;

        // Header (69 bytes)
        span[offset++] = node.NodeType;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), node.Version);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), node.KeyCount);
        offset += 2;
        node.NodeContentHash.AsSpan(0, 32).CopyTo(span.Slice(offset));
        offset += 32;
        node.PrevChainHash.AsSpan(0, 32).CopyTo(span.Slice(offset));
        offset += 32;

        // Keys (KeyCount × 32 bytes)
        for (int i = 0; i < node.KeyCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), node.Keys[i].Part1);
            offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), node.Keys[i].Part2);
            offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), node.Keys[i].Part3);
            offset += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset), node.Keys[i].Part4);
            offset += 8;
        }

        // Child offsets ((KeyCount + 1) × 8 bytes)
        for (int i = 0; i < childCount; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), node.ChildOffsets[i]);
            offset += 8;
        }

        // Child hashes ((KeyCount + 1) × 32 bytes)
        for (int i = 0; i < childCount; i++)
        {
            node.ChildHashes[i].AsSpan(0, 32).CopyTo(span.Slice(offset));
            offset += 32;
        }

        return buf;
    }

    public static BTreeInternalNode DeserializeInternal(byte[] data)
    {
        var span = data.AsSpan();
        int offset = 0;

        var node = new BTreeInternalNode();
        node.NodeType = span[offset++];
        node.Version = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
        offset += 2;
        node.KeyCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
        offset += 2;
        node.NodeContentHash = span.Slice(offset, 32).ToArray();
        offset += 32;
        node.PrevChainHash = span.Slice(offset, 32).ToArray();
        offset += 32;

        int childCount = node.KeyCount + 1;

        // Keys
        node.Keys = new EmailHashedID[node.KeyCount];
        for (int i = 0; i < node.KeyCount; i++)
        {
            node.Keys[i] = new EmailHashedID(
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 24)));
            offset += 32;
        }

        // Child offsets
        node.ChildOffsets = new long[childCount];
        for (int i = 0; i < childCount; i++)
        {
            node.ChildOffsets[i] = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset));
            offset += 8;
        }

        // Child hashes
        node.ChildHashes = new byte[childCount][];
        for (int i = 0; i < childCount; i++)
        {
            node.ChildHashes[i] = span.Slice(offset, 32).ToArray();
            offset += 32;
        }

        return node;
    }

    public static byte[] SerializeIndexRoot(IndexRoot root)
    {
        var buf = new byte[IndexRoot.PayloadSize];
        var span = buf.AsSpan();
        int offset = 0;

        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), root.RootNodeBlockOffset);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), root.EntryCount);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset), root.TreeHeight);
        offset += 2;
        root.RootNodeHash.AsSpan(0, 32).CopyTo(span.Slice(offset));
        offset += 32;
        root.PreviousRootHash.AsSpan(0, 32).CopyTo(span.Slice(offset));
        offset += 32;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset), root.PreviousRootOffset);

        return buf;
    }

    public static IndexRoot DeserializeIndexRoot(byte[] data)
    {
        var span = data.AsSpan();
        int offset = 0;

        var root = new IndexRoot();
        root.RootNodeBlockOffset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset));
        offset += 8;
        root.EntryCount = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset));
        offset += 8;
        root.TreeHeight = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset));
        offset += 2;
        root.RootNodeHash = span.Slice(offset, 32).ToArray();
        offset += 32;
        root.PreviousRootHash = span.Slice(offset, 32).ToArray();
        offset += 32;
        root.PreviousRootOffset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset));

        return root;
    }
}
