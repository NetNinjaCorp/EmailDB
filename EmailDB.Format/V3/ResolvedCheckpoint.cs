namespace EmailDB.Format.V3;

/// <summary>
/// One root of a Checkpoint after the reader has confirmed where it actually lives
/// (EmailDB_FileFormat_Spec.md Section 10.1): the durable <see cref="BlockId"/>
/// paired with the <see cref="Offset"/> the block was found at.
/// <see cref="HintWasStale"/> records whether the Checkpoint's offset hint was
/// trusted directly (false) or had to be re-resolved through the resolution chain
/// because the block at the hint did not carry the expected BlockId (true) — a
/// stale hint is never an error by itself.
/// </summary>
public sealed class ResolvedRoot
{
    /// <summary>The 16-byte ULID of the resolved root block.</summary>
    public required byte[] BlockId { get; init; }

    /// <summary>The file offset the block was actually found at (verified hint or re-resolved).</summary>
    public required long Offset { get; init; }

    /// <summary>Entire on-disk block size including the footer.</summary>
    public required long TotalBlockLength { get; init; }

    /// <summary>True when the Checkpoint's offset hint was stale and the block was re-resolved by BlockId.</summary>
    public required bool HintWasStale { get; init; }
}

/// <summary>One entry of a resolved Checkpoint's secondary-index table: its kind and resolved root.</summary>
public sealed class ResolvedSecondaryRoot
{
    /// <summary>Which index this entry names.</summary>
    public required BTreeIndexKind IndexKind { get; init; }

    /// <summary>The resolved root of that index.</summary>
    public required ResolvedRoot Root { get; init; }
}

/// <summary>
/// A validated Checkpoint together with every root resolved to a concrete on-disk
/// location (EmailDB_FileFormat_Spec.md Section 10.1). The <see cref="Checkpoint"/>
/// has already passed the block checksum, structural, and FileId cross-checks; each
/// named root has been located by its offset hint (or re-resolved by BlockId when
/// the hint was stale). Absent roots (<see cref="CheckpointRootPointer.None"/>,
/// e.g. the KeyStore of an unencrypted file or the previous-checkpoint link of the
/// first Checkpoint) resolve to null.
/// </summary>
public sealed class ResolvedCheckpoint
{
    /// <summary>The validated Checkpoint payload.</summary>
    public required Checkpoint Checkpoint { get; init; }

    /// <summary>Resolved folder-tree root, or null when absent.</summary>
    public ResolvedRoot? FolderTreeRoot { get; init; }

    /// <summary>Resolved primary-index root, or null when absent.</summary>
    public ResolvedRoot? PrimaryIndexRoot { get; init; }

    /// <summary>Resolved BlockLocationIndex root, or null when absent.</summary>
    public ResolvedRoot? LocationIndexRoot { get; init; }

    /// <summary>Resolved Metadata root, or null when absent.</summary>
    public ResolvedRoot? MetadataRoot { get; init; }

    /// <summary>Resolved KeyStore root, or null when absent (unencrypted file).</summary>
    public ResolvedRoot? KeyStoreRoot { get; init; }

    /// <summary>Resolved previous-Checkpoint pointer, or null for the first Checkpoint.</summary>
    public ResolvedRoot? PreviousCheckpoint { get; init; }

    /// <summary>Resolved secondary-index roots (date, FTS, bloom, future), in Checkpoint order.</summary>
    public required IReadOnlyList<ResolvedSecondaryRoot> SecondaryIndexes { get; init; }
}
