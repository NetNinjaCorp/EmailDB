# EmailDB File Format Specification (v2)

## 1. Overview

EmailDB stores email data, indexes, folder structures, and encryption keys within a single append-only file. The format is block-based with per-block checksums and optional per-block encryption. New data is always appended; updates write a new block version, leaving the old version intact until compaction.

## 2. Block Format

Every block follows this layout:

```
+-----------------------------+-------------------+----------------------------------------+
| Field                       | Size (Bytes)      | Description                            |
+-----------------------------+-------------------+----------------------------------------+
| Header Magic                | 8                 | 0xEE411DBBD114EEUL                     |
| Version                     | 2                 | Block format version (2)               |
| Block Type                  | 1                 | Enum (see Section 3)                   |
| Flags                       | 1                 | Bit field (see Section 2.1)            |
| Payload Encoding            | 1                 | Enum (see Section 2.3)                 |
| Key Epoch                   | 2                 | DEK epoch for encryption (0-65535)     |
| Timestamp                   | 8                 | UTC ticks (creation time)              |
| Block ID                    | 16                | ULID (128-bit)                         |
| Payload Length               | 8                 | Length of payload data in bytes         |
| Header Checksum             | 16                | BLAKE3-128 of all header fields above  |
+-----------------------------+-------------------+----------------------------------------+
| Payload Data                | Variable          | Block content (serialized)             |
| Payload Checksum            | 16                | BLAKE3-128 of payload data             |
+-----------------------------+-------------------+----------------------------------------+
| Footer Magic                | 8                 | ~HEADER_MAGIC                          |
| Total Block Length           | 8                 | Size of entire block (inc. footer)     |
+-----------------------------+-------------------+----------------------------------------+
```

- **Header size:** 47 bytes
- **Total fixed overhead:** 91 bytes per block
- **Checksum algorithm:** BLAKE3, truncated to 128 bits (16 bytes)

### 2.1 Flags Bit Layout

| Bit   | Name       | Values                                           |
|-------|------------|--------------------------------------------------|
| 0     | Encrypted  | 0 = plaintext, 1 = AES-256-GCM encrypted        |
| 1     | Compressed | 0 = none, 1 = compressed per Payload Encoding    |
| 2     | Tombstone  | 0 = live block, 1 = logically deleted            |
| 3     | Checkpoint | 0 = normal block, 1 = checkpoint block           |
| 4-7   | Reserved   | Must be 0                                        |

### 2.2 Key Epoch

A 2-byte unsigned integer (0-65535) identifying which Data Encryption Key (DEK) encrypted the payload. When Flags bit 0 is 0, Key Epoch is 0. See [Encryption](docs/Encryption.md) for the full key management model.

### 2.3 Block ID (ULID)

Block IDs are 128-bit ULIDs: 48-bit millisecond UTC timestamp in the high bits, 80 bits of cryptographic randomness in the low bits. Properties:

- **Lexicographically sortable** in creation order as raw bytes
- **Monotonic per-process** (randomness incremented within the same millisecond)
- **Globally unique** across processes and machines (no coordination needed)
- **Sync-friendly** -- delta computation is a single ULID comparison

### 2.4 Payload Encoding

| Value | Name     | Description                     |
|-------|----------|---------------------------------|
| 1     | Protobuf | Google Protocol Buffers         |
| 3     | Json     | System.Text.Json                |
| 4     | RawBytes | Unstructured byte payload       |

## 3. Block Types

```
Metadata            = 0      Global file metadata, root pointers
WAL                 = 1      Write-ahead log entries
FolderTree          = 2      Folder hierarchy definition
Folder              = 3      Folder content and email references
Segment             = 4      Data segment
Cleanup             = 5      Compaction tracking
BTreeLeaf           = 6      B+-tree leaf node (key-value pairs)
BTreeInternal       = 7      B+-tree internal node (routing keys)
IndexRoot           = 8      B+-tree root pointer
EmailContent        = 9      Email body/MIME content (Tier 3)
KeyStore            = 10     Encrypted DEK table
Checkpoint          = 11     Crash consistency + fast open
EmailMetadata       = 12     Full email headers/envelope (Tier 2)
FolderMeta          = 13     Page index per folder
FolderDeltaLog      = 14     Append-only change log per folder

// Reserved for future phases
FTSSegmentMeta      = 15     Full-text search segment metadata
FTSTermDictionary   = 16     Full-text search term dictionary
FTSPostingList      = 17     Full-text search posting list
FTSSearchRoot       = 18     Full-text search root pointer
BloomFilter         = 19     Bloom filter for existence checks
EmbeddingContent    = 20     Vector embedding data
VectorIndexNode     = 21     Vector index tree node
VectorIndexRoot     = 22     Vector index root pointer
```

### 3.1 Checkpoint Block

Written as the final step of every mutation batch. This is the commit point.

**Payload:**

| Field                       | Type    | Description                              |
|-----------------------------|---------|------------------------------------------|
| FormatVersion               | ushort  | Format version at checkpoint time        |
| CheckpointSequence          | ulong   | Monotonic counter across checkpoints     |
| FolderTreeRootBlockId       | Ulid    | Root of folder hierarchy                 |
| PrimaryIndexRootBlockId     | Ulid    | Root of primary BTree index              |
| MetadataBlockId             | Ulid    | Current metadata block                   |
| KeyStoreBlockId             | Ulid    | Current key store block                  |
| PreviousCheckpointBlockId   | Ulid    | Chain to prior checkpoint                |
| FileUUID                    | Guid    | Identifies this database instance        |
| LiveBlockCount              | long    | Total live blocks at checkpoint time     |
| Timestamp                   | long    | Checkpoint creation time (UTC ticks)     |

Estimated payload: ~130 bytes.

**Write protocol:**

1. Write all mutation blocks (email content, BTree nodes, folder pages, etc.)
2. Write updated Metadata block if needed
3. Write Checkpoint block **last** -- this commits the batch
4. Anything after the last valid Checkpoint is uncommitted and discarded on recovery

**Open protocol (fast open):**

1. Seek to end of file
2. Scan backward for the last valid Checkpoint (footer magic + checksum verification)
3. Read Checkpoint -- all root pointers are immediately available
4. No full sequential scan required

**Fallback:** If no Checkpoint is found (v1 file or corruption), fall back to full sequential scan.

## 4. Operations

- **Initialize:** Creates a new file with initial Metadata, Header, and Checkpoint blocks.
- **Write:** Appends a new block to the end of the file. Updates in-memory block index.
- **Read:** Reads a block by BlockId using the in-memory `Dictionary<Ulid, long>` for offset resolution. Verifies checksums. Decrypts payload if Encrypted flag is set, using the DEK identified by Key Epoch.
- **Scan:** Sequential read of all blocks to rebuild the block index. Used only as fallback when no valid Checkpoint exists.
- **Open:** Backward scan for last Checkpoint, then immediate access via root pointers.
- **Compaction:** Creates a new file containing only live blocks, discarding old versions. The in-memory offset map is rebuilt from the new file (BTree leaves reference BlockIds, not offsets).

## 5. Immutability Model

| Component | Mutable? | Notes |
|-----------|----------|-------|
| EmailContent blocks | Immutable | Never overwritten, never re-encrypted, never removed by compaction |
| BTree nodes | Immutable on disk | Copy-on-write: mutations write new nodes, old nodes become dead |
| IndexRoot | Append-only | New root appended per BTree mutation batch |
| Checkpoint | Append-only | New checkpoint per mutation batch |
| Metadata | Append-only | New version appended when root pointers change |
| KeyStore | Append-only | New version on key rotation |
| Folder pages | Append-only | New pages written on delta compile; old pages become dead |
| FolderDeltaLog | Append-only | Consumed and discarded when compiled into pages |
| File offsets | Runtime-only | `Dictionary<Ulid, long>`, never stored on disk in indexes |
