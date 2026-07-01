---
created: '2026-02-23'
id: EPIC-EMDB-8
points: null
priority: must
status: archived
tags:
- btree
- index
- kv-store
- hash-chain
- integrity
- core
target_date: null
title: Append-Only B+-Tree KV Index with Hash Chaining
updated: '2026-07-01'
---

Replace the abandoned ZoneTree integration (EPIC-EMDB-3) with a custom append-only, hash-chained B+-tree index for persistent email KV storage.

**Architecture**: CouchDB-style copy-on-write B+-tree stored as blocks in the .emdb file. Nodes are never modified in place — mutations append new node copies and cascade to a new root. No sibling pointers (avoids rewrite cascades).

**Key specs**:
- 4096-byte nodes stored as standard .emdb blocks (BlockType.BTreeLeaf, BTreeInternal, IndexRoot)
- Custom binary serialization (not protobuf — fixed-size 32B keys + 16B values)
- BLAKE3 hash chaining: each block includes hash of previous block written
- Merkle tree: internal nodes carry child node hashes, root hash = integrity of entire index
- WAL-buffered writes with batch flush to reduce write amplification
- IndexRoot block appended after each flush for crash recovery
- Capacity: height 4 handles ~13.6M entries (55 fan-out with Merkle, 82 entries/leaf)

**Key types**: EmailHashedID (32B SHA-3 key) → BlockLocation (16B: offset + length)

**Hash algorithm**: BLAKE3-256 via Blake3.NET (SIMD-accelerated, 4-10x faster than SHA-256). SHA-3 retained for EmailHashedID content hashing.

**Recovery**: Backward scan for last valid IndexRoot → verify chain hash → replay WAL entries written after it.

**Replaces**: EPIC-EMDB-3 (ZoneTree Integration) — archived. Eliminates ZoneTree.FullTextSearch NuGet dependency and ~1000 lines of commented-out adapter code.

**Integration**: Sits between CacheManager/RawBlockManager (below) and EmailManager (above). Semantic search (EPIC-EMDB-7) shares the EmailHashedID key space but is otherwise independent.