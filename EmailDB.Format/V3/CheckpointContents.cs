namespace EmailDB.Format.V3;

/// <summary>
/// The roots and accounting a single mutation batch commits at a Checkpoint
/// (EmailDB_FileFormat_Spec.md Sections 10.1, 10.3): the folder tree, primary
/// index, BlockLocationIndex, metadata, and KeyStore roots (each a verified
/// ULID+offset hint), the generic secondary-index table, and the live/dead
/// accounting that drives compaction without scanning (Section 11.2).
///
/// <para>This is the writer-facing half of a <see cref="Checkpoint"/>: the caller
/// supplies the roots it has already appended (as buffered, not-yet-durable
/// blocks) plus the counters, and <see cref="CheckpointWriter"/> stamps the
/// writer-owned fields — <see cref="Checkpoint.FormatVersion"/>,
/// <see cref="Checkpoint.CheckpointSequence"/>, <see cref="Checkpoint.FileId"/>,
/// and <see cref="Checkpoint.PreviousCheckpoint"/> — before it serializes and
/// commits the Checkpoint block. An absent root (e.g. the KeyStore of an
/// unencrypted file) is <see cref="CheckpointRootPointer.None"/>.</para>
/// </summary>
public sealed class CheckpointContents
{
    /// <summary>Root of the folder tree; <see cref="CheckpointRootPointer.None"/> when absent.</summary>
    public required CheckpointRootPointer FolderTreeRoot { get; init; }

    /// <summary>Root of the primary email index (IndexKind 0).</summary>
    public required CheckpointRootPointer PrimaryIndexRoot { get; init; }

    /// <summary>Root of the BlockLocationIndex (IndexKind 1, spec Section 7).</summary>
    public required CheckpointRootPointer LocationIndexRoot { get; init; }

    /// <summary>Root of the current Metadata block.</summary>
    public required CheckpointRootPointer MetadataRoot { get; init; }

    /// <summary>Root of the current KeyStore block; <see cref="CheckpointRootPointer.None"/> for unencrypted files.</summary>
    public required CheckpointRootPointer KeyStoreRoot { get; init; }

    /// <summary>Every other index root (date, FTS, bloom, future), keyed by IndexKind. Empty by default.</summary>
    public IReadOnlyList<CheckpointSecondaryIndex> SecondaryIndexes { get; init; } =
        Array.Empty<CheckpointSecondaryIndex>();

    /// <summary>Number of live blocks in the file (non-negative).</summary>
    public required long LiveBlockCount { get; init; }

    /// <summary>Live payload byte count (non-negative; drives compaction with <see cref="DeadByteCount"/>).</summary>
    public required long LiveByteCount { get; init; }

    /// <summary>Dead payload byte count reclaimable by compaction (non-negative).</summary>
    public required long DeadByteCount { get; init; }
}
