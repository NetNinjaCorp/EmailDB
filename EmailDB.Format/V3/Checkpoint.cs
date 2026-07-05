namespace EmailDB.Format.V3;

/// <summary>
/// In-memory model of a Checkpoint block payload (BlockType 9), the single commit
/// point of a v3 file (EmailDB_FileFormat_Spec.md Section 10.1). A Checkpoint names
/// every root that defines a consistent snapshot — folder tree, primary index,
/// BlockLocationIndex, metadata, KeyStore, and the previous Checkpoint — plus a
/// generic secondary-index table and the live/dead byte accounting that drives
/// compaction without scanning (spec Section 11.2).
///
/// <para>Layout (all multi-byte integers little-endian, spec Section 4;
/// byte serialization is <see cref="CheckpointSerializer"/>):</para>
///
/// <code>
///   FormatVersion            (ushort, 2)
///   CheckpointSequence       (ulong,  8)   — monotonic across checkpoints
///   FileId                   (Ulid,  16)   — must match the superblock
///   FolderTreeRoot           (Ulid 16 + long 8)
///   PrimaryIndexRoot         (Ulid 16 + long 8)   — IndexKind 0
///   LocationIndexRoot        (Ulid 16 + long 8)   — IndexKind 1 (Section 7)
///   MetadataRoot             (Ulid 16 + long 8)
///   KeyStoreRoot             (Ulid 16 + long 8)
///   PreviousCheckpoint       (Ulid 16 + long 8)   — checkpoint chain
///   SecondaryIndexCount      (ushort, 2)
///   SecondaryIndexes[]       ({ IndexKind ushort 2, BlockId Ulid 16, Offset long 8 } × count)
///   LiveBlockCount           (long, 8)
///   LiveByteCount            (long, 8)
///   DeadByteCount            (long, 8)
/// </code>
///
/// <para>Every ULID+offset pair is a verified hint (<see cref="CheckpointRootPointer"/>):
/// the ULID is the durable pointer, the offset a hint the reader confirms and
/// re-resolves on mismatch. The write protocol and reader with fsync ordering and
/// offset-hint verification are the follow-on task (US-EMDB-72-6); this type is a
/// plain payload holder those consume directly.</para>
///
/// <para><b>Monotonic CheckpointSequence and walkable chain.</b> Each Checkpoint
/// carries a <see cref="CheckpointSequence"/> strictly greater than its predecessor
/// and a <see cref="PreviousCheckpoint"/> pointer to that predecessor, so recovery
/// can walk the chain newest-to-oldest (spec Sections 10.1, 13). The first
/// Checkpoint of a file uses <see cref="CheckpointRootPointer.None"/> for
/// <see cref="PreviousCheckpoint"/>. Enforcing monotonicity is the writer's job
/// (US-EMDB-72-6); this model faithfully carries whatever sequence it is given.</para>
/// </summary>
public sealed class Checkpoint
{
    /// <summary>Size of the <see cref="FileId"/> field: a 16-byte ULID.</summary>
    public const int FileIdSize = UlidGenerator.UlidSize;

    /// <summary>Largest number of secondary-index entries a Checkpoint can carry (SecondaryIndexCount is a ushort).</summary>
    public const int MaxSecondaryIndexCount = ushort.MaxValue;

    /// <summary>Format version stamped into the payload.</summary>
    public required ushort FormatVersion { get; init; }

    /// <summary>Monotonic sequence across checkpoints (spec Section 10.1).</summary>
    public required ulong CheckpointSequence { get; init; }

    /// <summary>The file's 16-byte ULID; must match the superblock on open (cross-check).</summary>
    public required byte[] FileId { get; init; }

    /// <summary>Root of the folder tree.</summary>
    public required CheckpointRootPointer FolderTreeRoot { get; init; }

    /// <summary>Root of the primary email index (IndexKind 0).</summary>
    public required CheckpointRootPointer PrimaryIndexRoot { get; init; }

    /// <summary>Root of the BlockLocationIndex (IndexKind 1, spec Section 7).</summary>
    public required CheckpointRootPointer LocationIndexRoot { get; init; }

    /// <summary>Root of the current Metadata block.</summary>
    public required CheckpointRootPointer MetadataRoot { get; init; }

    /// <summary>Root of the current KeyStore block; <see cref="CheckpointRootPointer.None"/> for unencrypted files.</summary>
    public required CheckpointRootPointer KeyStoreRoot { get; init; }

    /// <summary>Pointer to the previous Checkpoint; <see cref="CheckpointRootPointer.None"/> for the first checkpoint.</summary>
    public required CheckpointRootPointer PreviousCheckpoint { get; init; }

    /// <summary>Generic table of every other index root (date, FTS, bloom, future), keyed by IndexKind.</summary>
    public required IReadOnlyList<CheckpointSecondaryIndex> SecondaryIndexes { get; init; }

    /// <summary>Number of live blocks in the file (non-negative).</summary>
    public required long LiveBlockCount { get; init; }

    /// <summary>Live payload byte count; with <see cref="DeadByteCount"/> drives compaction triggers (spec Section 11.2). Non-negative.</summary>
    public required long LiveByteCount { get; init; }

    /// <summary>Dead payload byte count reclaimable by compaction (spec Section 11.2). Non-negative.</summary>
    public required long DeadByteCount { get; init; }
}
