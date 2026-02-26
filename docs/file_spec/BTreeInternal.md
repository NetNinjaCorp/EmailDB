# BTreeInternal Block (BlockType = 7)

Internal (routing) node of the append-only B+-tree index. Contains separator keys, child offsets, and Merkle hashes for integrity verification.

## Block ID

Range-allocated: `60,000,000,000,000` + counter.

## Payload (Custom Binary)

Serialized by `BTreeNodeSerializer.SerializeInternal()`. All fields are fixed-size with `BinaryPrimitives` (little-endian).

### Node Header (69 bytes)

```
Offset  Size  Field
──────  ────  ─────
0       1     NodeType           (byte, identifies node as internal)
1       2     Version            (uint16 LE)
3       2     KeyCount           (uint16 LE, number of separator keys)
5       32    NodeContentHash    (BLAKE3-256 of this node's keys/children)
37      32    PrevChainHash      (BLAKE3-256 of previous block written)
```

### Keys Section (32 bytes each)

`KeyCount` separator keys, immediately after the header.

```
Per key (32 bytes):
  Part1 (uint64 LE) | Part2 (uint64 LE) | Part3 (uint64 LE) | Part4 (uint64 LE)
```

### Child Offsets Section (8 bytes each)

`KeyCount + 1` child file offsets, immediately after the keys.

```
Per child offset (8 bytes):
  ChildOffset (int64 LE, file offset of the child node block)
```

### Child Hashes Section (32 bytes each)

`KeyCount + 1` Merkle hashes, immediately after the child offsets. Each hash is the BLAKE3-256 of the child node's content.

```
Per child hash (32 bytes):
  ChildHash (byte[32], BLAKE3-256 of child node content)
```

### Capacity

Per-key overhead: 32 (key) + 8 (child offset) + 32 (child hash) = 72 bytes.
Plus one extra child: 8 (offset) + 32 (hash) = 40 bytes.

| Constant | Value | Derivation |
|----------|-------|------------|
| MaxPayload | 4,036 bytes | Budget for node content |
| MaxKeys | 54 | (4036 - 69 - 40) / 72 |
| MaxChildren | 55 | MaxKeys + 1 |

### Total Payload Size

`69 + (KeyCount × 32) + ((KeyCount + 1) × 8) + ((KeyCount + 1) × 32)` bytes.

## Merkle Tree Integrity

The child hashes form a Merkle tree from the root down to the leaves. Verification modes:

- **Per-path**: Read root → follow path to target leaf, verify each child hash against its parent's stored hash. Cost: O(tree_height).
- **Full tree**: Traverse all nodes, verify every child hash. Cost: O(total_nodes).

The IndexRoot block stores the root node hash, anchoring the Merkle tree.

## Encryption Policy

**Not encrypted** under the Default policy. Keys are opaque SHA3-256 hashes; child offsets and hashes are internal pointers.

## Source Reference

- `BTreeInternalNode.cs` — model
- `BTreeNodeSerializer.SerializeInternal()` / `DeserializeInternal()` — binary format
- `BTreeIndex.cs` — tree operations
