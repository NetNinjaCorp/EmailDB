# Segment Block (BlockType = 4)

General-purpose data segment block. Used for storing arbitrary named data segments within the file.

## Block ID

Range-allocated: `20,000,000,000,000` + counter.

## Payload (Protobuf)

| Field | Type | Description |
|-------|------|-------------|
| SegmentId | int64 | Unique segment identifier. |
| SegmentData | byte[] | Raw segment data. |
| FileName | string | Logical file name for this segment. |
| FileOffset | int64 | Logical offset within the segment's file. |
| ContentLength | int32 | Length of the content in bytes. |
| SegmentTimestamp | int64 | When this segment version was created. |
| IsDeleted | bool | Soft deletion flag. |
| Version | uint32 | Version number for this segment. |
| Metadata | Dictionary\<string, string\> | Optional key-value metadata. |

## Computed Properties

- `SegmentFileGroup`: `SegmentId / 1000` — groups segments for file organization.

## Encryption Policy

**Encrypted** under the Default policy.

## Source Reference

- `SegmentContent.cs`
- `SegmentManager.cs`
