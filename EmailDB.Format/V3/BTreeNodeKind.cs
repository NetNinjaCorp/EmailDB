namespace EmailDB.Format.V3;

/// <summary>
/// Generic B+-tree node kind (EmailDB_FileFormat_Spec.md Section 6), stored in
/// byte 0 of the 12-byte node header. Leaf nodes are carried in BlockType 4
/// (BTreeLeaf) blocks and internal nodes in BlockType 5 (BTreeInternal) blocks.
/// </summary>
public enum BTreeNodeKind : byte
{
    /// <summary>Leaf node: EntryCount × (KeySize + ValueSize) entries, sorted by key.</summary>
    Leaf = 0,

    /// <summary>
    /// Internal node: EntryCount routing keys of KeySize, then EntryCount + 1
    /// child records of ValueSize.
    /// </summary>
    Internal = 1,
}
