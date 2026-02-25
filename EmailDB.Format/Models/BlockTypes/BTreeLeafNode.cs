namespace EmailDB.Format.Models.BlockTypes;

/// <summary>
/// Represents a single entry in a B+-tree leaf node.
/// Maps an email hash key to its content block location.
/// Fixed size: 48 bytes (32 key + 8 offset + 8 blockId).
/// </summary>
public struct LeafEntry
{
    public const int Size = 48;

    /// <summary>The email hash used as the B+-tree search key (32 bytes).</summary>
    public EmailHashedID Key;

    /// <summary>File offset of the email content block (8 bytes).</summary>
    public long BlockOffset;

    /// <summary>Block ID of the email content block (8 bytes).</summary>
    public long BlockId;
}

/// <summary>
/// Binary layout for a B+-tree leaf node.
/// Header: NodeType(1) + Version(2) + EntryCount(2) + NodeContentHash(32) + PrevChainHash(32) = 69 bytes.
/// Followed by EntryCount × LeafEntry (48 bytes each).
/// Maximum 82 entries within a 4036-byte payload.
/// </summary>
public class BTreeLeafNode
{
    public const int HeaderSize = 69;
    public const int MaxPayload = 4036;
    public const int MaxEntries = (MaxPayload - HeaderSize) / LeafEntry.Size; // 82
    public const int MinEntries = MaxEntries / 2; // 41 — underflow threshold for non-root leaves

    public byte NodeType { get; set; }
    public ushort Version { get; set; }
    public ushort EntryCount { get; set; }
    public byte[] NodeContentHash { get; set; } = new byte[32];
    public byte[] PrevChainHash { get; set; } = new byte[32];
    public LeafEntry[] Entries { get; set; } = Array.Empty<LeafEntry>();
}
