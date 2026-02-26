# EmailDB — Project Overview

## What
EmailDB is a custom binary file format and storage engine for efficient email data storage. All data — metadata, email content, folder structures, and indexes — lives in a single `.emdb` file using an append-only, block-based architecture.

## Tech Stack
- **Language:** C# (.NET 9.0)
- **Serialization:** Protocol Buffers (protobuf-net) for block content, custom binary for B+-tree nodes
- **KV Index:** Custom append-only B+-tree with BLAKE3 Merkle integrity and monotonic sequence counter for recovery (EPIC-EMDB-8)
- **Search:** Five-phase strategy — address trigram index, listing page scan, date BTree, vector embeddings (sidecar `.emdb.vec`), bloom filters
- **Encryption:** AES-256-GCM per-block encryption with KEK/DEK key-wrapping and multi-epoch rotation (EPIC-EMDB-9)
- **Hashing:** SHA-3 (content identity — EmailHashedID), BLAKE3 (structural integrity — Merkle tree + block checksums)
- **Integrity:** BLAKE3-128 checksums on block headers/payloads, BLAKE3 Merkle tree on B+-tree nodes
- **Testing:** Custom test suite + unit tests + BenchmarkDotNet benchmarks

## Key Decision: EmailDB over SQLite (ADR-007)
Comprehensive benchmarking at 1K/10K/100K scale confirmed EmailDB as the right approach:
- **10x faster** bulk inserts, **69x faster** single inserts
- **7x faster** point lookups, **6x faster** folder listing
- **6x faster** deletes, **2x faster** moves
- Only weakness: search (no index yet) — solved by embedding search (EPIC-EMDB-7)

## Key Decision: Self-built B+-tree over ZoneTree (ADR-011)
ZoneTree (LSM-tree KV store) was evaluated but rejected:
- Required implementing 3 complex adapter interfaces (~1000 lines) to map ZoneTree's folder-of-files model to our single-file block store
- External dependency risk: ZoneTree API changes could break adapters
- Append-only B+-tree (CouchDB model) fits our block format natively — nodes are just blocks
- BLAKE3 Merkle tree integrity provides tamper detection that ZoneTree doesn't offer
- Height 4-5 B+-tree handles 10M+ entries with 4-5 block reads per lookup

## Projects
| Project | Purpose |
|---------|---------|
| `EmailDB.Format` | Core library — block management, caching, B+-tree index, email ops |
| `EmailDB.Format.Protobuf` | Protobuf serialization for block payloads |
| `EmailDB.Benchmark.SQLite` | BenchmarkDotNet suite comparing EmailDB vs SQLite+FTS5 |
| `EmailDB.Benchmark.SearchStrategy` | Search algorithm benchmarks (trigram, listing scan, bloom filters) |
| `EmailDB.Testing` | Integration / functional test suite |
| `EmailDB.UnitTests` | Unit tests with benchmarks |
| `EmailDB.Testing.FileFormatBenchmark` | Serialization format benchmarks |
| `EmailDB.Testing.RawBlocks` | Raw block testing |
| `NetNinja.Testing.BlockManager` | Block manager testing |

## Current State
- Branch: `InitialDev` — active development
- Core block format implemented (91-byte fixed overhead: header + BLAKE3-128 checksums + footer)
- Custom append-only B+-tree index implemented with Merkle integrity and monotonic sequence recovery
- AES-256-GCM encryption system with KEK/DEK key-wrapping architecture designed
- Three-tier email model designed: packed folder listing pages (Tier 1), email metadata (Tier 2), raw content (Tier 3)
- Five-phase search strategy designed: address trigram index, listing page scan, date BTree, vector embeddings, bloom filters
- Active-to-backup sync designed: ULID high-water mark for content, FolderVersion for folders
- Tiered compaction designed: BTree node compaction (L1), folder page rebuild (L2), full file compaction (L3)
- Layered architecture: RawBlockManager → CacheManager → BTreeIndex → EmailManager
- Protobuf serialization via `IBlockContentSerializer` interface (ADR-001, ADR-006)
- **Known bugs**: RawBlockManager footer int32/int64 mismatch (US-EMDB-21), CacheManager position corruption (US-EMDB-22)
- **Next milestone**: Fix core bugs (EPIC-EMDB-1), then implement three-tier folder listing, then search indexes, then sync
