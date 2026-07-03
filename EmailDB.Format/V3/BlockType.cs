namespace EmailDB.Format.V3;

/// <summary>
/// v3 block type registry (EmailDB_FileFormat_Spec.md Section 5).
/// Fresh, gapless numbering; an ID, once shipped in a release, is never reused
/// for a different meaning. Values 22-239 are reserved; 240-254 are
/// experimental/vendor and never appear in release files.
/// </summary>
public enum BlockType : byte
{
    /// <summary>Global file metadata (non-bootstrap; the superblock holds bootstrap).</summary>
    Metadata = 0,

    /// <summary>Write-ahead log block (spec Section 10.4).</summary>
    WAL = 1,

    /// <summary>Folder hierarchy definition.</summary>
    FolderTree = 2,

    /// <summary>Dead-block accounting for compaction.</summary>
    Cleanup = 3,

    /// <summary>Generic B+-tree leaf node (spec Section 6).</summary>
    BTreeLeaf = 4,

    /// <summary>Generic B+-tree internal node.</summary>
    BTreeInternal = 5,

    /// <summary>B+-tree root descriptor (carries IndexKind).</summary>
    IndexRoot = 6,

    /// <summary>Tier 3: raw MIME body, attachments.</summary>
    EmailContent = 7,

    /// <summary>Encrypted DEK table (spec Section 9.2).</summary>
    KeyStore = 8,

    /// <summary>Commit point + fast-open root table (spec Section 10).</summary>
    Checkpoint = 9,

    /// <summary>Tier 2: full RFC 5322 headers, MIME structure, Preview.</summary>
    EmailMetadata = 10,

    /// <summary>Per-folder page index (FolderVersion counter).</summary>
    FolderPageDirectory = 11,

    /// <summary>Tier 1: packed listing records (~80 emails/page).</summary>
    FolderPage = 12,

    /// <summary>Folder change log (chained append-only blocks).</summary>
    FolderDeltaLog = 13,

    /// <summary>Trigram/FTS segment metadata.</summary>
    FTSSegmentMeta = 14,

    /// <summary>Trigram/FTS term dictionary.</summary>
    FTSTermDictionary = 15,

    /// <summary>Trigram/FTS posting list.</summary>
    FTSPostingList = 16,

    /// <summary>Trigram/FTS root pointer.</summary>
    FTSSearchRoot = 17,

    /// <summary>Per-folder existence filter.</summary>
    BloomFilter = 18,

    /// <summary>Vector embedding data (.emdb.vec sidecar).</summary>
    EmbeddingContent = 19,

    /// <summary>HNSW/IVF node (.emdb.vec sidecar).</summary>
    VectorIndexNode = 20,

    /// <summary>Vector index root (.emdb.vec sidecar).</summary>
    VectorIndexRoot = 21,
}
