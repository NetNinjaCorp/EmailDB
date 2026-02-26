# Metadata Block (BlockType = 0)

System block that stores file-level pointers to other key structures. There is one active Metadata block at a time; updates append a new version.

## Block ID

Fixed: `1` (system block).

A second system block with ID `0` stores the [HeaderContent](#header-content) (file version and bootstrap offsets).

## Payload (Protobuf)

| Field | Type | Description |
|-------|------|-------------|
| WALOffset | int64 | File offset of the current WAL block. -1 if no WAL. |
| FolderTreeOffset | int64 | File offset of the current FolderTree block. -1 if none. |
| SegmentOffsets | Dictionary\<string, int64\> | Named segment offsets (keyed by segment name). |
| OutdatedOffsets | List\<int64\> | File offsets of blocks that have been superseded (dead blocks). |

## Header Content (BlockId = 0)

The very first block written to a new file. Stores bootstrap offsets so the system can locate the Metadata and FolderTree without scanning.

| Field | Type | Description |
|-------|------|-------------|
| FileVersion | int32 | Format version number. |
| FirstMetadataOffset | int64 | File offset of the first Metadata block. |
| FirstFolderTreeOffset | int64 | File offset of the first FolderTree block. |
| FirstCleanupOffset | int64 | File offset of the first Cleanup block. |

## Encryption Policy

**Never encrypted** under any policy. The Metadata block contains only internal file pointers — no email data. It must be readable before the encryption key is available to bootstrap file opening.

## Source Reference

- `MetadataContent.cs`
- `HeaderContent.cs`
- `MetadataManager.cs`
