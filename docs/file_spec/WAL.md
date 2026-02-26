# WAL Block (BlockType = 1)

Write-ahead log for buffered operations. Stores pending mutations that haven't yet been flushed to their target structures (BTree, folders, etc.).

## Block ID

Fixed: `3` (system block). Updated in-place by overwriting the existing block.

## Payload (Protobuf)

| Field | Type | Description |
|-------|------|-------------|
| Entries | Dictionary\<string, List\<WALEntry\>\> | Pending entries grouped by category. |
| NextWALOffset | int64 | File offset of the next WAL block in a chain. -1 if this is the tail. |
| CategoryOffsets | Dictionary\<string, int64\> | Per-category offset pointers for efficient partial reads. |

### WALEntry

| Field | Type | Description |
|-------|------|-------------|
| SerializedKey | byte[] | Serialized key (e.g., EmailHashedID bytes). |
| SerializedValue | byte[] | Serialized value (e.g., BlockOffset + BlockId). |
| OpIndex | int64 | Operation sequence number for ordering. |
| Category | string | Category identifier for grouping entries. |
| SegmentId | int64 | Associated segment, if applicable. |

## BTree WAL

The BTree index uses a separate WAL mechanism managed by `BTreeWALManager`. This WAL is written as raw bytes at a reserved file offset (not as a standard WAL block). It buffers BTree insert entries (48 bytes each: 32B key + 8B offset + 8B blockId) and flushes to the BTree when the buffer reaches 82 entries (one full leaf).

## Encryption Policy

**Encrypted** under the Default policy. WAL entries may contain email-related data.

## Source Reference

- `WALContent.cs`
- `BTreeWALManager.cs`
