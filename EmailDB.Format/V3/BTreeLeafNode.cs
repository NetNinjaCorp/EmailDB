namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of a generic B+-tree leaf node (EmailDB_FileFormat_Spec.md
/// Section 6). On disk the body is <c>EntryCount × (KeySize + ValueSize)</c>
/// bytes of fixed-width entries sorted by key, preceded by the 12-byte node
/// header. Keys and values are opaque fixed-width byte strings here; their
/// interpretation (spec Section 6.1) belongs to the owning index. A value width
/// of 0 is valid (the Date index stores keys only).
/// </summary>
public sealed class BTreeLeafNode
{
    /// <summary>Which index this node belongs to (spec Section 6.1).</summary>
    public BTreeIndexKind IndexKind { get; set; }

    /// <summary>Bytes per key (must be at least 1).</summary>
    public byte KeySize { get; set; }

    /// <summary>Bytes per value (0 for key-only indexes such as Date).</summary>
    public ushort ValueSize { get; set; }

    /// <summary>
    /// The leaf entries, sorted strictly ascending by key (unsigned lexicographic
    /// byte order). <see cref="BTreeNodeSerializer.SerializeLeaf"/> enforces the
    /// ordering and the declared widths.
    /// </summary>
    public List<BTreeLeafEntry> Entries { get; init; } = new();
}

/// <summary>
/// One fixed-width leaf entry: <c>KeySize</c> key bytes followed by
/// <c>ValueSize</c> value bytes.
/// </summary>
/// <param name="Key">Exactly the node's declared KeySize bytes.</param>
/// <param name="Value">Exactly the node's declared ValueSize bytes (empty when ValueSize is 0).</param>
public readonly record struct BTreeLeafEntry(byte[] Key, byte[] Value);
