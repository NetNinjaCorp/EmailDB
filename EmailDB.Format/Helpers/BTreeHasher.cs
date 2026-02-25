using Blake3;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format.Helpers;

/// <summary>
/// Computes BLAKE3-256 hashes for B+-tree node content.
/// Used for tamper detection via NodeContentHash fields.
/// Hashes cover the data portion of nodes (entries, keys, child offsets, child hashes),
/// excluding header metadata and the hash fields themselves.
/// </summary>
public static class BTreeHasher
{
    /// <summary>
    /// Computes the BLAKE3 content hash for a leaf node's entries.
    /// </summary>
    public static byte[] ComputeLeafContentHash(BTreeLeafNode node)
    {
        using var ms = new MemoryStream(node.EntryCount * LeafEntry.Size);
        using var writer = new BinaryWriter(ms);

        for (int i = 0; i < node.EntryCount; i++)
        {
            ref readonly var entry = ref node.Entries[i];
            writer.Write(entry.Key.Part1);
            writer.Write(entry.Key.Part2);
            writer.Write(entry.Key.Part3);
            writer.Write(entry.Key.Part4);
            writer.Write(entry.BlockOffset);
            writer.Write(entry.BlockId);
        }

        writer.Flush();
        return Hasher.Hash(ms.ToArray()).AsSpan().ToArray();
    }

    /// <summary>
    /// Computes the BLAKE3 content hash for an internal node's keys, child offsets, and child hashes.
    /// </summary>
    public static byte[] ComputeInternalContentHash(BTreeInternalNode node)
    {
        int childCount = node.KeyCount + 1;
        int estimatedSize = node.KeyCount * BTreeInternalNode.KeySize
                          + childCount * BTreeInternalNode.OffsetSize
                          + childCount * BTreeInternalNode.HashSize;

        using var ms = new MemoryStream(estimatedSize);
        using var writer = new BinaryWriter(ms);

        for (int i = 0; i < node.KeyCount; i++)
        {
            writer.Write(node.Keys[i].Part1);
            writer.Write(node.Keys[i].Part2);
            writer.Write(node.Keys[i].Part3);
            writer.Write(node.Keys[i].Part4);
        }

        for (int i = 0; i < childCount; i++)
        {
            writer.Write(node.ChildOffsets[i]);
        }

        for (int i = 0; i < childCount; i++)
        {
            writer.Write(node.ChildHashes[i], 0, 32);
        }

        writer.Flush();
        return Hasher.Hash(ms.ToArray()).AsSpan().ToArray();
    }

    /// <summary>
    /// Computes the BLAKE3 hash of arbitrary data. Returns a 32-byte digest.
    /// </summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return Hasher.Hash(data).AsSpan().ToArray();
    }
}
