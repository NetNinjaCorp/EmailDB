namespace EmailDB.Format.V3;

/// <summary>
/// Operation kind carried by a single <see cref="WalEntry"/>
/// (EmailDB_FileFormat_Spec.md Section 10.4). One byte discriminates the logical
/// mutation a WAL entry replays; the entry's <see cref="WalEntry.Key"/>,
/// <see cref="WalEntry.BlockId"/>, and <see cref="WalEntry.Aux"/> carry the
/// operands.
///
/// <para>Only the values defined here are valid on disk: the deserializer rejects
/// an unknown op byte as a malformed payload (spec Section 13) rather than
/// replaying an operation it cannot interpret.</para>
/// </summary>
public enum WalOpKind : byte
{
    /// <summary>
    /// Index upsert: bind <see cref="WalEntry.Key"/> to <see cref="WalEntry.BlockId"/>
    /// (last write wins). Replays as an index insert/update.
    /// </summary>
    Insert = 1,

    /// <summary>
    /// Index delete: remove <see cref="WalEntry.Key"/>. <see cref="WalEntry.BlockId"/>
    /// is unused (all-zero) and <see cref="WalEntry.Aux"/> is empty.
    /// </summary>
    Delete = 2,

    /// <summary>
    /// Folder-tree mutation: <see cref="WalEntry.Key"/> identifies the folder and
    /// <see cref="WalEntry.Aux"/> carries the op-specific detail (e.g. the new
    /// folder page / delta). <see cref="WalEntry.BlockId"/> names the block the
    /// mutation produced when applicable.
    /// </summary>
    FolderOp = 3,
}
