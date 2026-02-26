# IndexRoot Block (BlockType = 8)

Points to the current root of the B+-tree index. A new IndexRoot is appended after every BTree flush. The most recent valid IndexRoot represents the latest committed state of the index.

## Block ID

Range-allocated: `70,000,000,000,000` + counter.

## Payload (Custom Binary, Fixed 90 bytes)

Serialized by `BTreeNodeSerializer.SerializeIndexRoot()`.

```
Offset  Size  Field
──────  ────  ─────
0       8     RootNodeBlockOffset   (int64 LE, file offset of the root BTree node)
8       8     EntryCount            (int64 LE, total entries in the tree)
16      2     TreeHeight            (uint16 LE, current tree height)
18      32    RootNodeHash          (BLAKE3-256 of the root node content)
50      32    PreviousRootHash      (BLAKE3-256 of previous IndexRoot; zero-filled if first)
82      8     PreviousRootOffset    (int64 LE, file offset of previous IndexRoot; -1 if none)
```

**Total payload: 90 bytes.**

## Recovery

On file open, the system scans backward from EOF for the last valid IndexRoot block:

1. Verify the block's BLAKE3-128 header/payload checksums
2. Load the root node from `RootNodeBlockOffset`
3. Optionally verify `RootNodeHash` matches the loaded root
4. Scan for WAL entries written after this IndexRoot
5. Replay any uncommitted WAL entries
6. Write a new IndexRoot if replay occurred

The `PreviousRootHash` and `PreviousRootOffset` fields form a backward chain of IndexRoot blocks, enabling verification of the flush history.

## Tree Height at Scale

| Email Count | Tree Height | Max Entries at Height |
|-------------|-------------|-----------------------|
| < 82 | 1 | 82 |
| < ~4,500 | 2 | 82 × 55 |
| < ~248K | 3 | 82 × 55² |
| < ~13.6M | 4 | 82 × 55³ |
| < ~750M | 5 | 82 × 55⁴ |

## Encryption Policy

**Not encrypted** under the Default policy. Contains only internal pointers and hashes.

## Source Reference

- `IndexRoot.cs` — model (`PayloadSize = 90`)
- `BTreeNodeSerializer.SerializeIndexRoot()` / `DeserializeIndexRoot()` — binary format
- `BTreeIndex.cs` — flush and recovery logic
