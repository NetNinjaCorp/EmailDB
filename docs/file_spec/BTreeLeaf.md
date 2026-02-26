# BTreeLeaf Block (BlockType = 6)

Leaf node of the append-only B+-tree index. Contains sorted key-value entries mapping `EmailHashedID` to block locations.

## Block ID

Range-allocated: `50,000,000,000,000` + counter.

## Payload (Custom Binary)

Serialized by `BTreeNodeSerializer.SerializeLeaf()`. All fields are fixed-size with `BinaryPrimitives` (little-endian).

### Node Header (69 bytes)

```
Offset  Size  Field
──────  ────  ─────
0       1     NodeType           (byte, identifies node as leaf)
1       2     Version            (uint16 LE)
3       2     EntryCount         (uint16 LE, number of entries in this node)
5       32    NodeContentHash    (BLAKE3-256 of this node's entries — tamper detection)
37      32    PrevChainHash      (BLAKE3-256 of previous block written — sequential chain)
```

### Entries (48 bytes each)

Immediately follow the header. `EntryCount` entries, sorted by key.

```
Offset  Size  Field
──────  ────  ─────
0       8     Key.Part1          (uint64 LE)
8       8     Key.Part2          (uint64 LE)
16      8     Key.Part3          (uint64 LE)
24      8     Key.Part4          (uint64 LE)
32      8     BlockOffset        (int64 LE, file offset of the email content block)
40      8     BlockId            (int64 LE, block ID of the email content block)
```

Total entry size: 48 bytes (32B EmailHashedID key + 8B offset + 8B block ID).

### Capacity

| Constant | Value | Derivation |
|----------|-------|------------|
| MaxPayload | 4,036 bytes | Budget for node content within a block |
| MaxEntries | 82 | (4036 - 69) / 48 |
| MinEntries | 41 | MaxEntries / 2 (underflow threshold, non-root) |

### Total Payload Size

`69 + (EntryCount × 48)` bytes. Maximum: `69 + (82 × 48) = 4,005 bytes`.

## Copy-on-Write

Leaf nodes are never modified in place. A mutation creates a new leaf block and rewrites the path up to the root. The old leaf becomes a dead block.

## Encryption Policy

**Not encrypted** under the Default policy. Leaf keys are SHA3-256 hashes (opaque, not reversible to email content). Block offsets/IDs are internal pointers.

## Source Reference

- `BTreeLeafNode.cs` — model
- `BTreeNodeSerializer.SerializeLeaf()` / `DeserializeLeaf()` — binary format
- `BTreeIndex.cs` — tree operations
