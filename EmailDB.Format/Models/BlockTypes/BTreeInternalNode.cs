namespace EmailDB.Format.Models.BlockTypes;

/// <summary>
/// Binary layout for a B+-tree internal node.
/// Header: NodeType(1) + Version(2) + KeyCount(2) + NodeContentHash(32) + PrevChainHash(32) = 69 bytes.
/// Followed by KeyCount × keys (32 bytes each), (KeyCount+1) × childOffsets (8 bytes each),
/// and (KeyCount+1) × childHashes (32 bytes each).
/// Maximum 54 keys / 55 children within a 4036-byte payload.
/// </summary>
public class BTreeInternalNode
{
    public const int HeaderSize = 69;
    public const int MaxPayload = 4036;
    public const int KeySize = 32;    // EmailHashedID
    public const int OffsetSize = 8;  // long
    public const int HashSize = 32;   // BLAKE3 / Merkle hash

    // Per-key overhead = 32 (key) + 8 (child offset) + 32 (child hash) = 72
    // Plus one extra child: 8 (offset) + 32 (hash) = 40
    // MaxKeys = (MaxPayload - HeaderSize - 40) / 72 = (4036 - 69 - 40) / 72 = 54
    public const int MaxKeys = (MaxPayload - HeaderSize - OffsetSize - HashSize) / (KeySize + OffsetSize + HashSize); // 54
    public const int MaxChildren = MaxKeys + 1; // 55

    public byte NodeType { get; set; }
    public ushort Version { get; set; }
    public ushort KeyCount { get; set; }
    public byte[] NodeContentHash { get; set; } = new byte[32];
    public byte[] PrevChainHash { get; set; } = new byte[32];

    /// <summary>Search keys separating child subtrees. Length = KeyCount.</summary>
    public EmailHashedID[] Keys { get; set; } = Array.Empty<EmailHashedID>();

    /// <summary>File offsets of child nodes. Length = KeyCount + 1.</summary>
    public long[] ChildOffsets { get; set; } = Array.Empty<long>();

    /// <summary>Merkle hashes of child nodes. Length = KeyCount + 1. Each hash is 32 bytes.</summary>
    public byte[][] ChildHashes { get; set; } = Array.Empty<byte[]>();
}
