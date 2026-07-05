namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of the 12-byte generic B+-tree node header
/// (EmailDB_FileFormat_Spec.md Section 6), stored at the start of every
/// BTreeLeaf/BTreeInternal block payload:
///
///   NodeKind (1) + NodeVersion (1) + IndexKind (2) + KeySize (1) +
///   ValueSize (2) + EntryCount (2) + Reserved (3, must be 0) = 12 bytes.
///
/// Key/value widths are declared here per node, so every index shares the same
/// node format and adding an index never changes the file format. Serialization
/// to/from the on-disk layout is handled by <see cref="BTreeNodeSerializer"/>.
/// </summary>
public sealed class BTreeNodeHeader
{
    /// <summary>0 = leaf, 1 = internal.</summary>
    public BTreeNodeKind NodeKind { get; set; }

    /// <summary>Node format version. Always 1 for this spec.</summary>
    public byte NodeVersion { get; set; } = BTreeNodeSerializer.CurrentNodeVersion;

    /// <summary>Which index this node belongs to (registry in spec Section 6.1).</summary>
    public BTreeIndexKind IndexKind { get; set; }

    /// <summary>Bytes per key.</summary>
    public byte KeySize { get; set; }

    /// <summary>Bytes per leaf value / internal child record.</summary>
    public ushort ValueSize { get; set; }

    /// <summary>
    /// Number of entries: leaf entries for a leaf, routing keys for an internal
    /// node (which then carries EntryCount + 1 child records).
    /// </summary>
    public ushort EntryCount { get; set; }

    /// <summary>Creates a copy that can be mutated without affecting the original.</summary>
    public BTreeNodeHeader Clone() => new()
    {
        NodeKind = NodeKind,
        NodeVersion = NodeVersion,
        IndexKind = IndexKind,
        KeySize = KeySize,
        ValueSize = ValueSize,
        EntryCount = EntryCount,
    };
}
