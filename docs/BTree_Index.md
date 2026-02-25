# BTree Primary Index

The primary index is a copy-on-write B+-tree that maps `EmailHashedID` to `BlockId`. It is the only structure used for point lookups of individual emails.

## What the BTree Stores

| Field | Size | Description |
|-------|------|-------------|
| EmailHashedID (key) | 32 bytes | SHA3-256 hash of MessageID + Date + Subject + From + To |
| BlockId (value) | 16 bytes | ULID pointing to the EmailContent block |
| **Total per entry** | **48 bytes** | |

The BTree does **not** store file offsets. BlockIds are the durable pointer; offsets are resolved at runtime via `Dictionary<Ulid, long>`.

## What the BTree Does NOT Do

- **Folder browsing** -- handled by folder listing pages (see [Folder Listing](Folder_Listing.md))
- **Display metadata** -- subject, sender, date are in folder listing pages (Tier 1) or EmailMetadata blocks (Tier 2)
- **Search** -- secondary BTree indexes and FTS handle search (see [Search](Search.md))
- **Recent email lookups** -- served from the WAL buffer in memory before flush

The BTree is consulted only when a user opens a specific email that has already been flushed from the WAL buffer into the tree.

## Node Structure

### Leaf Nodes (BlockType = 6)

| Component | Size | Description |
|-----------|------|-------------|
| Node header | 69 bytes | NodeType, Version, EntryCount, NodeContentHash, PrevChainHash |
| Entries | up to 4,036 bytes | Array of (EmailHashedID + BlockId) pairs |
| **Max entries** | **82** | 4,036 / 48 = 82 (with ~70% fill: avg ~57) |
| **Min entries** | **41** | Underflow threshold (non-root) |

### Internal Nodes (BlockType = 7)

| Component | Size | Description |
|-----------|------|-------------|
| Node header | 69 bytes | NodeType, Version, KeyCount, NodeContentHash, PrevChainHash |
| Keys + child offsets + child hashes | up to 4,036 bytes | Routing keys with child pointers |
| **Max keys / children** | **54 / 55** | |

### IndexRoot (BlockType = 8)

Fixed 90-byte payload linking to the current BTree root:

| Field | Description |
|-------|-------------|
| RootNodeBlockOffset | File offset of the root node |
| EntryCount | Total entries in the tree |
| TreeHeight | Current tree height |
| RootNodeHash | BLAKE3 hash of the root node |
| PreviousRootHash | Hash of prior IndexRoot (integrity chain) |
| PreviousRootOffset | Offset of prior IndexRoot |

## Copy-on-Write Model

The BTree uses the CouchDB copy-on-write pattern:

1. A mutation (insert/delete) rewrites only the root-to-leaf path
2. Unchanged subtrees are shared -- their blocks remain untouched
3. A new IndexRoot block is appended, pointing to the new root
4. Old nodes become dead blocks, reclaimed at compaction

This means every BTree mutation produces `tree_height` new blocks. At height 4, that is 4 new blocks per insert.

## Hash Chain Integrity

Every node stores:
- **NodeContentHash** -- BLAKE3 of the node's entries/keys (tamper detection)
- **PrevChainHash** -- BLAKE3 linking to the previous version of this node

Internal nodes additionally store child hashes, forming a Merkle tree. The IndexRoot chain links consecutive root versions. This allows verification of the entire tree from the root down.

## WAL Buffering

The BTree is **not** updated on every email write. The `BTreeWALManager` buffers inserts:

1. Email write appends an EmailContent block + a 48-byte WAL entry
2. WAL entries accumulate in memory (crash-safe via on-disk WAL region)
3. When the buffer reaches **82 entries** (one full leaf), a flush triggers
4. Flush sorts entries by key and inserts them into the BTree via COW path rewrites

**Per-email write cost:** `email_size + 48 bytes WAL entry + ~232 bytes folder delta` -- all sequential appends. No BTree traversal on the write path.

## Tree Height at Scale

| Email Count | Tree Height | Leaf Nodes | Total Index Size |
|-------------|-------------|------------|------------------|
| 10,000      | 3           | 176        | ~511 KB          |
| 100,000     | 4           | 1,755      | ~4.9 MB          |
| 1,000,000   | 4           | 17,544     | ~49 MB           |
| 10,000,000  | 5           | 175,439    | ~494 MB          |
| 100,000,000 | 5           | 1,754,386  | ~4.82 GB         |

## Serialization

BTree nodes use a custom binary serializer (`BTreeNodeSerializer`) with `BinaryPrimitives` for fixed-size fields. This is faster than Protobuf for the fixed-layout node structures. All other block types use Protobuf or JSON via the `iBlockContentSerializer` interface.
