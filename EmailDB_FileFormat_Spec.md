# EmailDB Custom File Format Specification

## 1. Overview

This document specifies the binary file format used by EmailDB for storing email data and associated indexes efficiently within a single file. The format is designed to be append-only, similar in concept to log-structured file systems or databases like ReFS, allowing for versioning of data blocks. It utilizes a block-based structure with checksums for integrity and incorporates ZoneTree for storing email content (Key/Value) and search indexes (Vector).

## 2. File Structure

-   **Single File:** All data, including metadata, email content, folder structures, and indexes, is stored within a single file.
-   **Append-Only:** New data is always appended to the end of the file. Updates involve writing a new version of a block, leaving the old version intact until compaction.
-   **Block-Based:** The file is composed of variable-length data blocks. Each block contains a specific type of information (metadata, folder structure, email data segment, index segment, etc.).
-   **Journaling/Versioning:** The append-only nature inherently supports journaling and versioning. Previous versions of blocks remain in the file, identified by their unique `BlockId`.
-   **Compaction:** A compaction process can be run to create a new file containing only the latest versions of active blocks, reclaiming space used by older or deleted blocks.

## 3. Block Format

Each block in the file follows this structure:

```
+-----------------------------+-------------------+----------------------+
| Field                       | Size (Bytes)      | Description          |
+-----------------------------+-------------------+----------------------+
| **Header**                  |                   |                      |
+-----------------------------+-------------------+----------------------+
| Header Magic                | 8                 | 0xEE411DBBD114EEUL   |
| Version                     | 2                 | Block format version |
| Block Type                  | 1                 | Enum (See Block Types)|
| Flags                       | 1                 | Reserved for future use |
| Payload Encoding            | 1                 | Enum (See Payload Encodings) |
| Timestamp                   | 8                 | UTC Ticks (Creation) |
| Block ID                    | 8                 | Unique ID for this block instance |
| Payload Length              | 8                 | Length of Payload Data |
| **Header Checksum**         | **4**             | **CRC32 of Header**  |
+-----------------------------+-------------------+----------------------+
| **Payload Data**            | Variable          | Block content (encoded) |
| **Payload Checksum**        | **4**             | **CRC32 of Payload** |
+-----------------------------+-------------------+----------------------+
| **Footer**                  |                   |                      |
+-----------------------------+-------------------+----------------------+
| Footer Magic                | 8                 | ~HEADER_MAGIC        |
| Total Block Length          | 8                 | Size of entire block |
+-----------------------------+-------------------+----------------------+
```

-   **Header Size:** 37 bytes (Magic + Version + Type + Flags + Encoding + Timestamp + ID + Length)
-   **Total Fixed Overhead:** 57 bytes (Header + Header Checksum + Payload Checksum + Footer)
-   **Checksum Algorithm:** CRC32 (using Force.Crc32 implementation)

## 4. Block Types (Enum)

The `Block Type` field in the header identifies the content of the payload. Key types include:

-   `Metadata`: Contains global information about the file, such as the ID of the root Folder Tree block, file version, etc. Typically, there is one primary metadata block, potentially updated by appending a new version.
-   `FolderTree`: Defines the hierarchy of email folders. Contains a list of folder names and their corresponding `BlockId`s pointing to `FolderContent` blocks.
-   `FolderContent`: Contains information specific to a folder, potentially including references (e.g., `BlockId`s) to email or index blocks associated with that folder. (Note: `BlockManager.cs` implies this, but details need refinement based on actual usage).
-   `ZoneTreeSegment_KV`: Stores a segment of the ZoneTree Key/Value store used for email bodies/metadata. The payload contains the serialized ZoneTree segment data.
-   `ZoneTreeSegment_Vector`: Stores a segment of the ZoneTree Vector store used for email search indexes. The payload contains the serialized ZoneTree segment data.
-   `FreeSpace`: (Optional) Could be used to mark blocks that have been superseded or deleted, aiding the compaction process.

*(Further block types might be defined as needed)*

## 4.5 Payload Encodings (Enum)

The `Payload Encoding` field specifies how the `Payload Data` is serialized.

- `Protobuf` = 1
- `CapnProto` = 2
- `Json` = 3
- `RawBytes` = 4 // For cases where payload is just raw data

*(Further encodings might be defined as needed)*

## 5. Payload Serialization (Protobuf) and ZoneTree Integration

The `Payload Data` section of each block is serialized using Google Protocol Buffers (Protobuf). Specific `.proto` files will define the message structures for each `Block Type`.

ZoneTree data is stored within the Protobuf payloads of dedicated block types:

-   **Email Data (Key/Value):**
    -   Stored in blocks of type `ZoneTreeSegment_KV`.
    -   The Protobuf message for this block type will contain fields to hold the serialized data representing a segment or page of the ZoneTree Key/Value B+ Tree.
    -   Keys could be email UIDs or other identifiers, and values could be the email content (MIME) or structured metadata, managed within the Protobuf structure.
-   **Index Data (Vector):**
    -   Stored in blocks of type `ZoneTreeSegment_Vector`.
    -   The Protobuf message for this block type will contain fields to hold the serialized data representing a segment or page of the ZoneTree Vector Log/B+ Tree used for indexing.
    -   This allows for efficient searching over email content or metadata.

The specific Protobuf message definitions will encapsulate the necessary ZoneTree segment data, likely treating the raw ZoneTree serialized output as `bytes` within the Protobuf message.

## 6. Operations

-   **Initialization:** Creates a new file, typically writing an initial `Metadata` block.
-   **Write:** Appends a new block (any type) to the end of the file. Updates `blockLocations` map in `RawBlockManager`.
-   **Read:** Reads a specific block by its `BlockId` using the `blockLocations` map to find its position and length. Verifies checksums upon reading.
-   **Scan:** Reads the file sequentially, identifying valid blocks by magic numbers and checksums to rebuild the `blockLocations` map if needed (e.g., on startup).
-   **Compaction:** Creates a new file containing only the latest versions of active blocks, discarding old versions and potentially reclaiming space. Replaces the original file upon successful completion.

## 7. Considerations

-   **Block ID Management:** The system needs a strategy for generating unique `BlockId`s. This could be a simple incrementing counter persisted in the `Metadata` block.
-   **Versioning Strategy:** While the format supports storing old versions, the application logic (using `BlockManager` and `CacheManager`) determines how these versions are accessed or presented. The current `RawBlockManager`'s `blockLocations` seems to store only the *latest* location for a given `BlockId`. If true versioning (accessing older states) is required, the mapping might need adjustment, perhaps incorporating timestamps or version numbers into the lookup, or storing a history of locations per logical entity ID.
-   **Concurrency:** The `RawBlockManager` uses a `ReaderWriterLockSlim` for thread safety during file access.
-   **Error Handling:** Checksum verification helps detect corruption. Robust error handling is needed for scenarios like checksum failures, incomplete blocks, or I/O errors.