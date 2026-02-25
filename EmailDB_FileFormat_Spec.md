# EmailDB Custom File Format Specification

## 1. Overview

This document specifies the binary file format used by EmailDB for storing email data and associated indexes efficiently within a single file. The format is designed to be append-only, similar in concept to log-structured file systems or databases like ReFS, allowing for versioning of data blocks. It utilizes a block-based structure with checksums for integrity and incorporates a BTree index for primary email lookups.

## 2. File Structure

-   **Single File:** All data, including metadata, email content, folder structures, and indexes, is stored within a single file.
-   **Append-Only:** New data is always appended to the end of the file. Updates involve writing a new version of a block, leaving the old version intact until compaction.
-   **Block-Based:** The file is composed of variable-length data blocks. Each block contains a specific type of information (metadata, folder structure, email data segment, index segment, etc.).
-   **Journaling/Versioning:** The append-only nature inherently supports journaling and versioning. Previous versions of blocks remain in the file, identified by their unique `BlockId`.
-   **Compaction:** A compaction process can be run to create a new file containing only the latest versions of active blocks, reclaiming space used by older or deleted blocks.

## 3. Block Format (v2)

Each block in the file follows this structure:

```
+-----------------------------+-------------------+----------------------------------------+
| Field                       | Size (Bytes)      | Description                            |
+-----------------------------+-------------------+----------------------------------------+
| **Header**                  |                   |                                        |
+-----------------------------+-------------------+----------------------------------------+
| Header Magic                | 8                 | 0xEE411DBBD114EEUL                     |
| Version                     | 2                 | Block format version (2)               |
| Block Type                  | 1                 | Enum (See Block Types)                 |
| Flags                       | 1                 | Bit field (See Flags Layout)           |
| Payload Encoding            | 1                 | Enum (See Payload Encodings)           |
| Key Epoch                   | 2                 | DEK epoch for encryption (0-65535)     |
| Timestamp                   | 8                 | UTC Ticks (Creation)                   |
| Block ID                    | 16                | ULID (128-bit)                         |
| Payload Length               | 8                 | Length of Payload Data                  |
| **Header Checksum**         | **16**            | **BLAKE3-128 of Header**               |
+-----------------------------+-------------------+----------------------------------------+
| **Payload Data**            | Variable          | Block content (encoded)                |
| **Payload Checksum**        | **16**            | **BLAKE3-128 of Payload**              |
+-----------------------------+-------------------+----------------------------------------+
| **Footer**                  |                   |                                        |
+-----------------------------+-------------------+----------------------------------------+
| Footer Magic                | 8                 | ~HEADER_MAGIC                          |
| Total Block Length           | 8                 | Size of entire block                   |
+-----------------------------+-------------------+----------------------------------------+
```

-   **Header Size:** 47 bytes (Magic + Version + Type + Flags + Encoding + KeyEpoch + Timestamp + ID + Length)
-   **Total Fixed Overhead:** 91 bytes (Header + Header Checksum + Payload Checksum + Footer)
-   **Checksum Algorithm:** BLAKE3-128 (truncated to 128-bit / 16 bytes)

### 3.1 Flags Bit Layout

The `Flags` byte is a defined bit field:

| Bit   | Name       | Values                                           |
|-------|------------|--------------------------------------------------|
| 0     | Encrypted  | 0 = plaintext, 1 = AES-GCM encrypted            |
| 1     | Compressed | 0 = none, 1 = compressed per Payload Encoding    |
| 2     | Tombstone  | 0 = live block, 1 = logically deleted            |
| 3     | Checkpoint | 0 = normal block, 1 = checkpoint block           |
| 4-7   | Reserved   | Must be 0 for forward compatibility              |

### 3.2 Key Epoch

The `Key Epoch` field is a 2-byte unsigned integer (0-65535) identifying which Data Encryption Key (DEK) was used to encrypt the block payload. When encryption is disabled (Flags bit 0 = 0), Key Epoch is 0. The dedicated 2-byte field replaces the previous approach of packing key epoch information into the Flags byte, providing a range of 0-65535 epochs instead of the previous 0-127 limit.

### 3.3 Block ID (ULID)

Block IDs are 128-bit ULIDs (Universally Unique Lexicographically Sortable Identifiers). A ULID consists of a 48-bit millisecond-precision UTC timestamp in the high bits, followed by 80 bits of cryptographic randomness in the low bits.

Key properties:

-   **Lexicographically sortable:** ULIDs sort in creation order when compared as raw bytes, enabling efficient range scans and ordered iteration without secondary indexes.
-   **Monotonic per-process:** Within a single process, ULID generation guarantees strict monotonic ordering even when multiple IDs are generated within the same millisecond (the random component is incremented).
-   **Globally unique:** The combination of millisecond timestamp and 80 bits of randomness makes collisions practically impossible across processes and machines.
-   **Sync-friendly:** Unlike auto-incrementing integers or range-based schemes, ULIDs generated on different machines do not conflict, making them suitable for replication scenarios.

This replaces the previous 8-byte `long` BlockId and range-based generation scheme. All on-disk references to block IDs use the full 16-byte ULID representation.

## 4. Block Types (Enum)

The `Block Type` field in the header identifies the content of the payload:

```
Metadata            = 0      // Global file information, root pointers
WAL                 = 1      // Write-ahead log entries
FolderTree          = 2      // Folder hierarchy definition
Folder              = 3      // Folder content and references
Segment             = 4      // Data segment
Cleanup             = 5      // Compaction/cleanup tracking
BTreeLeaf           = 6      // BTree leaf node (key-value pairs)
BTreeInternal       = 7      // BTree internal node (routing keys)
IndexRoot           = 8      // BTree index root pointer
EmailContent        = 9      // Email body/MIME content
KeyStore            = 10     // Encryption key store (DEK table)
Checkpoint          = 11     // Crash consistency + fast open
EmailMetadata       = 12     // Full email headers/envelope (Tier 2)
FolderMeta          = 13     // Page index per folder
FolderDeltaLog      = 14     // Append-only change log per folder
FTSSegmentMeta      = 15     // Full-text search segment metadata
FTSTermDictionary   = 16     // Full-text search term dictionary
FTSPostingList      = 17     // Full-text search posting list
FTSSearchRoot       = 18     // Full-text search root pointer
BloomFilter         = 19     // Bloom filter for existence checks
EmbeddingContent    = 20     // Vector embedding data
VectorIndexNode     = 21     // Vector index tree node
VectorIndexRoot     = 22     // Vector index root pointer
```

### Block Type Descriptions

-   `Metadata`: Contains global information about the file, such as the ID of the root Folder Tree block, file version, etc. Typically, there is one primary metadata block, potentially updated by appending a new version.
-   `FolderTree`: Defines the hierarchy of email folders. Contains a list of folder names and their corresponding `BlockId`s pointing to `Folder` blocks.
-   `Folder`: Contains information specific to a folder, including references (`BlockId`s) to email or index blocks associated with that folder.
-   `BTreeLeaf` / `BTreeInternal` / `IndexRoot`: The primary BTree index for email lookups. Leaf entries contain `(EmailHashedID, BlockId)` pairs. Internal nodes contain routing keys. The IndexRoot block points to the current root of the BTree.
-   `EmailContent`: Stores the email body and MIME content.
-   `EmailMetadata`: Stores full email headers and envelope data (Tier 2 storage, separate from the content block for efficient header-only queries).
-   `KeyStore`: Contains the encrypted DEK table, mapping key epochs to their encrypted Data Encryption Keys.
-   `Checkpoint`: Crash consistency marker and fast-open anchor. See Section 4.1.
-   `FolderMeta`: Page index for a folder, containing page directory and sync primitives.
-   `FolderDeltaLog`: Append-only per-folder change log recording Add/Delete/Move/FlagChange operations.
-   `FTSSegmentMeta` through `FTSSearchRoot`: Full-text search index structures (reserved for future phases).
-   `BloomFilter`: Probabilistic existence filter for efficient negative lookups.
-   `EmbeddingContent` / `VectorIndexNode` / `VectorIndexRoot`: Vector embedding storage and index structures (reserved for future phases).

### 4.1 Checkpoint Block

A Checkpoint block is a small block written as the final step of any mutation batch. It serves as the commit point for the batch and provides the authoritative set of root pointers for the database state.

**CheckpointContent payload:**

```
FormatVersion:              ushort      // Format version at checkpoint time
CheckpointSequence:         ulong       // Monotonic counter across checkpoints
FolderTreeRootBlockId:      Ulid        // Root of the folder hierarchy
PrimaryIndexRootBlockId:    Ulid        // Root of the primary BTree index
MetadataBlockId:            Ulid        // Current metadata block
KeyStoreBlockId:            Ulid        // Current key store block
PreviousCheckpointBlockId:  Ulid        // Chain to prior checkpoint
FileUUID:                   Guid        // Identifies this database instance
LiveBlockCount:             long        // Total live blocks at checkpoint time
Timestamp:                  long        // Checkpoint creation time (UTC ticks)
```

Estimated payload size: ~130 bytes.

**Write protocol:**

1.  Write all mutation blocks (email content, BTree nodes, folder pages, etc.)
2.  Write updated Metadata block if needed
3.  Write Checkpoint block **last** -- this commits the batch
4.  Anything after the last valid Checkpoint is uncommitted and can be discarded on recovery

**Open protocol (fast open):**

1.  Seek to end of file
2.  Scan backward for the last valid Checkpoint block (look for footer magic, verify checksums)
3.  Read Checkpoint -- all root pointers are immediately available
4.  Done. No full sequential scan required.

**Fallback:** If no Checkpoint is found (e.g., a v1 file or severe corruption), fall back to full sequential scan to rebuild state. This preserves backward compatibility.

## 4.5 Payload Encodings (Enum)

The `Payload Encoding` field specifies how the `Payload Data` is serialized.

- `Protobuf` = 1
- `Json` = 3
- `RawBytes` = 4 // For cases where payload is just raw data

*(Further encodings might be defined as needed)*

## 5. Payload Serialization (Protobuf)

The `Payload Data` section of each block is serialized using Google Protocol Buffers (Protobuf). Specific `.proto` files or code-first Protobuf models define the message structures for each `Block Type`.

The primary BTree index stores email lookups:

-   **Leaf entries:** `(EmailHashedID, BlockId)` -- 32 + 16 = 48 bytes per entry. Block IDs are the authoritative reference; file offsets are resolved at runtime via an in-memory `Dictionary<Ulid, long>` mapping built from the Checkpoint's block inventory.
-   **Internal nodes:** Routing keys with child `BlockId` pointers.

The specific Protobuf message definitions encapsulate the necessary data for each block type, treating raw serialized output as `bytes` within the Protobuf message where appropriate.

## 6. Operations

-   **Initialization:** Creates a new file, writing an initial `Metadata` block and an initial `Checkpoint` block.
-   **Write:** Appends a new block (any type) to the end of the file. Updates the in-memory `blockLocations` map.
-   **Read:** Reads a specific block by its `BlockId` using the `blockLocations` map to find its file offset. Verifies checksums upon reading. Decrypts payload if the Encrypted flag is set, using the DEK identified by the block's Key Epoch.
-   **Scan:** Reads the file sequentially, identifying valid blocks by magic numbers and checksums to rebuild the `blockLocations` map. Used as a fallback when no valid Checkpoint is found.
-   **Open (fast):** Seeks to end of file, scans backward for the last valid Checkpoint block, and reads root pointers directly. See Section 4.1.
-   **Compaction:** Creates a new file containing only the latest versions of active blocks, discarding old versions and reclaiming space. The in-memory offset map is rebuilt from the new file rather than rewriting BTree leaf entries (since leaves reference BlockIds, not offsets).

## 7. Sync Model

The format supports one-way replication (Active to Backup). Multi-master sync is out of scope for v1.

-   **FolderVersion:** Each folder maintains a `FolderVersion` counter (`ulong`) that increments when delta log entries are compiled into folder chain blocks. Backup replicas compare `FolderVersion` values to determine which folders need updating.
-   **Active-to-Backup replication:** The backup periodically compares its per-folder `FolderVersion` against the primary. For any folder where the primary's version is higher, the backup fetches the delta log entries (or full folder state if the delta window has expired) and applies them locally.
-   **Secondary machine writes:** Secondary (backup) machines do not write directly to the storage format. Instead, they submit mutation actions (Add, Delete, Move, FlagChange) to the primary via an application-layer actions channel. The primary applies these actions to its own file and the changes propagate to backups through the normal replication flow.

## 8. Considerations

-   **Block ID Generation:** Block IDs are generated as ULIDs with a monotonic guarantee per-process. Within a single process, the ULID generator ensures strict ordering even when multiple IDs are requested within the same millisecond by incrementing the random component. No centralized counter or range allocation is needed.
-   **Versioning Strategy:** While the format supports storing old versions, the application logic (using `BlockManager` and `CacheManager`) determines how these versions are accessed or presented. The `blockLocations` map stores only the *latest* location for a given `BlockId`. If true versioning (accessing older states) is required, the mapping might need adjustment, perhaps incorporating timestamps or version numbers into the lookup, or storing a history of locations per logical entity ID.
-   **Concurrency:** The `RawBlockManager` uses a `ReaderWriterLockSlim` for thread safety during file access.
-   **Error Handling:** Checksum verification helps detect corruption. Robust error handling is needed for scenarios like checksum failures, incomplete blocks, or I/O errors.
