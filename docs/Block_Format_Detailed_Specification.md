# EmailDB Block Format Detailed Specification v1.0

## Overview

This document provides the definitive, byte-level specification for the EmailDB block format. Every block in an EmailDB file MUST conform to this specification exactly.

## Block Structure

Each block consists of four parts:
1. **Header** (37 bytes)
2. **Header Checksum** (4 bytes)  
3. **Payload Data** (variable length)
4. **Payload Checksum** (4 bytes)
5. **Footer** (16 bytes)

**Total Fixed Overhead: 61 bytes** (37 + 4 + 4 + 16)

## Detailed Header Format (37 bytes)

The header is exactly 37 bytes with the following structure:

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 0 | 8 | Magic Number | uint64 | MUST be 0xEE411DBBD114EEUL (little-endian) |
| 8 | 2 | Version | uint16 | Block format version (currently 1) |
| 10 | 1 | Block Type | uint8 | See Block Types section |
| 11 | 1 | Flags | uint8 | Reserved, MUST be 0 |
| 12 | 1 | Payload Encoding | uint8 | See Payload Encodings section |
| 13 | 8 | Timestamp | int64 | UTC ticks when block was created |
| 21 | 8 | Block ID | int64 | Unique identifier for this block |
| 29 | 8 | Payload Length | int64 | Length of payload data in bytes |

### Header Checksum (4 bytes)

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 37 | 4 | Header CRC32 | uint32 | CRC32 of bytes 0-36 |

## Payload Section

### Payload Data (variable length)

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 41 | Variable | Payload | byte[] | Encoded payload data |

### Payload Checksum (4 bytes)

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 41 + payload_length | 4 | Payload CRC32 | uint32 | CRC32 of payload bytes |

## Footer Format (16 bytes)

| Offset | Size | Field | Type | Description |
|--------|------|-------|------|-------------|
| 45 + payload_length | 8 | Footer Magic | uint64 | MUST be ~0xEE411DBBD114EEUL |
| 53 + payload_length | 8 | Total Block Length | int64 | Total size of entire block |

## Block Types

```
enum BlockType : byte {
    Metadata = 0,           // System metadata
    WAL = 1,               // Write-ahead log
    FolderTree = 2,        // Folder hierarchy
    Folder = 3,            // Individual folder data
    Segment = 4,           // Data segment
    Cleanup = 5,           // Cleanup/maintenance data
    ZoneTreeSegment_KV = 6,    // ZoneTree Key-Value segment
    ZoneTreeSegment_Vector = 7, // ZoneTree Vector index segment
    FreeSpace = 8          // Marked as free for reuse
}
```

## Payload Encodings

```
enum PayloadEncoding : byte {
    Protobuf = 1,   // Google Protocol Buffers
    CapnProto = 2,  // Cap'n Proto serialization
    Json = 3,       // JSON text encoding
    RawBytes = 4    // No encoding, raw binary data
}
```

## Endianness

All multi-byte values are stored in **little-endian** format.

## Checksums

- Algorithm: CRC32 (ITU-T V.42 polynomial)
- Implementation: Force.Crc32.Crc32Algorithm
- Header checksum covers bytes 0-36 (the entire header)
- Payload checksum covers all payload bytes
- Empty payloads (length 0) have a checksum of 0x00000000

## Magic Numbers

- Header Magic: `0xEE411DBBD114EEUL`
- Footer Magic: `0x11BEE244E2EB1116UL` (bitwise NOT of header magic)

## Validation Requirements

A block is considered valid if and only if:

1. Header magic equals 0xEE411DBBD114EEUL
2. Header checksum matches computed CRC32 of header bytes
3. Block type is a valid enum value
4. Payload encoding is a valid enum value
5. Payload length >= 0
6. Payload checksum matches computed CRC32 of payload
7. Footer magic equals ~0xEE411DBBD114EEUL
8. Total block length equals (61 + payload_length)

## Error Handling

If any validation check fails:
- The block MUST be considered corrupt
- Readers SHOULD attempt to find the next valid block by scanning for header magic
- The corruption SHOULD be logged with block offset and failure reason

## Example Block (Hex Dump)

Here's a minimal valid block with empty payload:

```
Offset    00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
00000000  EE 14 D1 BB 1D 41 EE 0E 01 00 00 00 01 00 00 00  |.....A..........|
00000010  00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00  |................|
00000020  00 00 00 00 00 XX XX XX XX 00 00 00 00 16 11 EB  |.....XXXX.......|
00000030  E2 44 E2 BE 11 3D 00 00 00 00 00 00 00           |.D...=.......|

Where:
- Bytes 0-7: Header magic
- Bytes 8-9: Version (1)
- Byte 10: Block type (0 = Metadata)
- Byte 11: Flags (0)
- Byte 12: Payload encoding (1 = Protobuf)
- Bytes 13-20: Timestamp (0)
- Bytes 21-28: Block ID (0)
- Bytes 29-36: Payload length (0)
- Bytes 37-40: Header CRC32 (marked as XX)
- Bytes 41-44: Payload CRC32 (0 for empty payload)
- Bytes 45-52: Footer magic
- Bytes 53-60: Total block length (61)
```

## Version History

- v1.0: Initial specification with full header structure including PayloadEncoding field