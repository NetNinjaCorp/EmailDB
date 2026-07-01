# EmailDB

A single-file, append-only email storage format with per-block encryption, BLAKE3 integrity verification, and B+-tree indexing. Designed for email archival and retrieval at scale (10M+ emails).

## Key Properties

- **Single file per shard** -- data, indexes, folders, and encryption keys in one `.emdb` file (~50 GB shards; rebuildable `.emdb.vec` search sidecar)
- **Append-only** -- blocks are never overwritten; the dual-slot superblock is the only (torn-write-safe) in-place structure
- **Per-block encryption** -- AES-256-GCM with KEK/DEK key wrapping, multi-epoch rotation, and AAD binding to block + file identity
- **Per-block integrity** -- BLAKE3-128 checksums on header and payload; Merkle-verified B+-tree indexes
- **Copy-on-write B+-trees** -- one generic node format serving the primary email index, BlockLocationIndex, and date index
- **Checkpoint-based recovery** -- O(log n) open via superblock pointer; full scans are disaster recovery only
- **ULID block IDs** -- 128-bit, temporally ordered, globally unique, sync-friendly; offsets are derived data
- **Active-to-Backup sync** -- one-way replication via ULID high-water marks and FolderVersion

## Documentation

| Document | Description |
|----------|-------------|
| [File Format Spec](EmailDB_FileFormat_Spec.md) | **Normative** v3 on-disk format: superblock, block layout, types, encryption, recovery contract |
| [BTree Index](docs/BTree_Index.md) | Generic node format, primary/location/date indexes, WAL flush, verification |
| [Folder Listing](docs/Folder_Listing.md) | Three-tier email model, folder pages, delta log |
| [Search](docs/Search.md) | Five-phase strategy: trigram FTS, page scan, date BTree, vectors, bloom filters |
| [Encryption](docs/Encryption.md) | KEK/DEK hierarchy, policies, password change/rotation, threat model |
| [Compaction](docs/Compaction.md) | Dead-block accounting, side-file swap, re-encryption |
| [Storage Estimates](docs/Storage_Estimates.md) | Overhead, index sizes, per-shard sizing |
| [Sync](docs/Sync.md) | Active-to-Backup replication, FolderVersion, actions channel |

## Project Structure

```
EmailDB.Format/                  Core storage engine
  FileManagement/                RawBlockManager, CacheManager, MetadataManager, etc.
  Models/BlockTypes/             Block content models (BTreeLeafNode, IndexRoot, etc.)
  Encryption/                    AES-GCM provider, KeyStore, key derivation
  Helpers/                       BlockIDGenerator, BTreeHasher, AsyncReaderWriterLock
  BTreeIndex.cs                  Copy-on-write B+-tree implementation
  BTreeNodeSerializer.cs         Binary node serialization
EmailDB.Format.Protobuf/        Protobuf serialization layer
EmailDB.UnitTests/               Unit and integration tests
EmailDB.Benchmark.SQLite/        Comparative benchmarks
```

## License

MIT License -- Copyright (c) 2025 NetNinja Corp
