# EMDB File Format Specification

This document defines the binary format of `.emdb` files. All multi-byte integers are little-endian.

## File Structure

An `.emdb` file is a sequence of contiguous blocks, optionally preceded by an [Encryption Header](Encryption_Header.md) if the file is encrypted.

```
[Encryption Header (optional, variable size)]
[Block 0]
[Block 1]
...
[Block N]
```

Blocks are append-only. Old versions of a block are never overwritten; they become dead blocks reclaimed during compaction.

## Block Format

Every block has the same outer structure: a fixed 84-byte overhead wrapping a variable-length payload.

```
Offset  Size   Field
──────  ────   ─────
0       8      HEADER_MAGIC    (0xEE411DBBD114EE, uint64)
8       2      Version         (uint16)
10      1      BlockType       (byte, see Block Types below)
11      1      Flags           (byte, see Flags Byte below)
12      8      Timestamp       (int64, Unix timestamp)
20      8      BlockId         (int64, see Block ID Allocation)
28      8      PayloadLength   (int64, byte count of payload)
────────────── (36 bytes total header) ──────────────
36      16     HeaderChecksum  (BLAKE3-128: first 16 bytes of BLAKE3 hash of bytes 0-35)
────────────── (52 bytes before payload) ──────────────
52      var    Payload         (PayloadLength bytes)
52+N    16     PayloadChecksum (BLAKE3-128 of payload bytes; 16 zero bytes if payload is empty)
────────────── Footer ──────────────
68+N    8      FOOTER_MAGIC    (~HEADER_MAGIC, uint64)
76+N    8      TotalBlockLength (int64 = 84 + PayloadLength)
```

| Constant | Value |
|----------|-------|
| `HEADER_MAGIC` | `0xEE411DBBD114EEUL` |
| `FOOTER_MAGIC` | `~HEADER_MAGIC` (`0x11BEE2448EEB11UL`) |
| `HeaderSize` | 36 bytes |
| `HeaderChecksumSize` | 16 bytes |
| `PayloadChecksumSize` | 16 bytes |
| `FooterSize` | 16 bytes (8 magic + 8 length) |
| **`TotalFixedOverhead`** | **84 bytes** |

### Checksum Algorithm

BLAKE3-128: compute the full BLAKE3 hash of the input, then take the first 16 bytes (128 bits). Uses the [Blake3.NET](https://www.nuget.org/packages/Blake3) library by xoofx (SIMD-accelerated: AVX2/AVX-512).

### Flags Byte

```
Bit 0:     Encrypted (0 = plaintext, 1 = payload is encrypted)
Bits 1-7:  KeyEpoch (0-127, identifies which DEK encrypted this block)
```

KeyEpoch is only meaningful when Encrypted = 1. See [KeyStore](KeyStore.md) for the DEK lookup mechanism.

## Block Types

| ID | Name | Serialization | Payload Doc |
|----|------|---------------|-------------|
| 0 | Metadata | Protobuf | [Metadata](Metadata.md) |
| 1 | WAL | Protobuf | [WAL](WAL.md) |
| 2 | FolderTree | Protobuf | [FolderTree](FolderTree.md) |
| 3 | Folder | Protobuf | [Folder](Folder.md) |
| 4 | Segment | Protobuf | [Segment](Segment.md) |
| 5 | Cleanup | Protobuf | [Cleanup](Cleanup.md) |
| 6 | BTreeLeaf | Custom binary | [BTreeLeaf](BTreeLeaf.md) |
| 7 | BTreeInternal | Custom binary | [BTreeInternal](BTreeInternal.md) |
| 8 | IndexRoot | Custom binary | [IndexRoot](IndexRoot.md) |
| 9 | EmailContent | Protobuf/JSON | [EmailContent](EmailContent.md) |
| 10 | KeyStore | Protobuf (KEK-encrypted) | [KeyStore](KeyStore.md) |

## Block ID Allocation

Block IDs are `int64` values partitioned into ranges by block type. Each range spans 10 trillion IDs.

### System Blocks (fixed IDs)

| Block | ID |
|-------|----|
| Header (Metadata) | 0 |
| Metadata | 1 |
| FolderTree | 2 |
| WAL | 3 |

### Dynamic Blocks (range-allocated)

| BlockType | Base ID | Range |
|-----------|---------|-------|
| Folder | 10,000,000,000,000 | 1T - 2T |
| Segment | 20,000,000,000,000 | 2T - 3T |
| Cleanup | 30,000,000,000,000 | 3T - 4T |
| (Custom) | 40,000,000,000,000 | 4T - 5T |
| BTreeLeaf | 50,000,000,000,000 | 5T - 6T |
| BTreeInternal | 60,000,000,000,000 | 6T - 7T |
| IndexRoot | 70,000,000,000,000 | 7T - 8T |
| EmailContent | 80,000,000,000,000 | 8T - 9T |

Within each range, IDs are monotonically increasing counters. On file open, the counter is recovered by scanning existing blocks and taking the max ID per range.

## Serialization

Payload encoding is determined by block type:

| Encoding | ID | Used By |
|----------|----|---------|
| Protobuf | 1 | Most block types (protobuf-net `[ProtoContract]` attributes) |
| JSON | 2 | Debug/interchange |
| RawBytes | 3 | Binary data passthrough |
| Custom binary | - | BTree nodes (BTreeLeaf, BTreeInternal, IndexRoot) |

BTree nodes use custom binary serialization via `BTreeNodeSerializer` with `BinaryPrimitives` for fixed-size fields. This avoids protobuf's variable-length encoding overhead on fixed-layout structures.

## Encryption

When encryption is enabled, the file begins with an [Encryption Header](Encryption_Header.md) before the first block. Encrypted block payloads have 28 bytes of additional overhead (12B nonce + 16B auth tag). See [Encryption Header](Encryption_Header.md) for the file-level format and [KeyStore](KeyStore.md) for key management.

## Integrity Model

Two independent integrity layers:

1. **Block-level**: BLAKE3-128 checksums on header and payload detect corruption or truncation.
2. **Index-level**: BLAKE3-256 Merkle tree on BTree nodes. Internal nodes carry child hashes; IndexRoot stores the root hash. Enables per-path or full-tree verification. See [BTreeInternal](BTreeInternal.md).

## EmailHashedID

The primary key for emails. 32 bytes (SHA3-256).

```
Input:  UTF8(MessageID + Date + Subject + From + To)
Output: SHA3-256 → 32 bytes, stored as 4 × uint64 LE
```

Stored in BTree entries, folder email lists, and WAL entries. Display format: zBase32 encoding.

## Source Reference

- `RawBlockManager.cs` — block I/O, checksums, scanning
- `Block.cs` — block model with flags/epoch helpers
- `BlockType.cs` — enum definition
- `BlockIdGenerator.cs` — ID allocation
- `BTreeNodeSerializer.cs` — BTree binary serialization
- `PayloadEncoding.cs` — encoding enum
