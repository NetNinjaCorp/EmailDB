namespace EmailDB.Format.V3;

/// <summary>
/// One entry of a Checkpoint's generic secondary-index table
/// (EmailDB_FileFormat_Spec.md Section 10.1): a <see cref="BTreeIndexKind"/>
/// tag paired with a verified offset hint (<see cref="Pointer"/>) to that index's
/// root block.
///
/// <para>The table is generic on purpose: date, FTS, bloom, and future indexes are
/// all recorded here without a Checkpoint format change — the folder tree, primary
/// index, and BlockLocationIndex roots have dedicated Checkpoint fields, everything
/// else lands in this table keyed by its <see cref="IndexKind"/> (spec Sections
/// 6.1, 10.1). <see cref="IndexKind"/> is therefore NOT restricted to the
/// registered kinds, matching <see cref="IndexRootSerializer"/> and
/// <see cref="BTreeNodeSerializer"/>.</para>
/// </summary>
public sealed class CheckpointSecondaryIndex
{
    /// <summary>Which index this entry names (registry in spec Section 6.1, but not restricted to it).</summary>
    public required BTreeIndexKind IndexKind { get; init; }

    /// <summary>Verified offset hint (ULID + offset) to this index's root block.</summary>
    public required CheckpointRootPointer Pointer { get; init; }

    /// <summary>Creates a secondary-index entry from a kind, root block ULID, and offset hint.</summary>
    /// <exception cref="ArgumentException"><paramref name="blockId"/> is not 16 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    public static CheckpointSecondaryIndex Create(BTreeIndexKind indexKind, byte[] blockId, long offset) =>
        new()
        {
            IndexKind = indexKind,
            Pointer = CheckpointRootPointer.Create(blockId, offset),
        };
}
