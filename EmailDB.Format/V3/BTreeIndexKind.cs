namespace EmailDB.Format.V3;

/// <summary>
/// IndexKind registry (EmailDB_FileFormat_Spec.md Section 6.1): which index a
/// generic B+-tree node belongs to. Key/value widths are declared per node in
/// its header, so new kinds never require a node format change. Values 4-99
/// are reserved; 100+ are experimental.
/// </summary>
public enum BTreeIndexKind : ushort
{
    /// <summary>
    /// Primary email index: EmailHashedID (32, SHA3-256) → BlockId (16).
    /// Internal child record: ChildBlockId (16) + ChildHash (32) = 48.
    /// </summary>
    PrimaryEmail = 0,

    /// <summary>
    /// BlockLocationIndex: BlockId (16) → Offset (8) + Length (8) = 16.
    /// Internal child record: ChildOffset (8) + ChildHash (32) = 40 —
    /// offset-addressed because this index cannot depend on itself.
    /// </summary>
    BlockLocation = 1,

    /// <summary>
    /// Date index: composite key DateTicks (8) ‖ BlockId (16) = 24, empty value.
    /// Internal child record: ChildBlockId (16) + ChildHash (32) = 48.
    /// </summary>
    Date = 2,

    /// <summary>Trigram/FTS trees (layout defined in docs/Search.md).</summary>
    Fts = 3,
}
