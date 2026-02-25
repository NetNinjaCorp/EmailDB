# EmailDB

A single-file, append-only email storage format with per-block encryption, BLAKE3 integrity verification, and B+-tree indexing. Designed for email archival and retrieval at scale (10M+ emails).

## Key Properties

- **Single file** -- all data, indexes, folders, and encryption keys in one `.emdb` file
- **Append-only** -- blocks are never overwritten; old versions remain until compaction
- **Per-block encryption** -- AES-256-GCM with multi-epoch key rotation
- **Per-block integrity** -- BLAKE3-128 checksums on both header and payload
- **Copy-on-write B+-tree** -- primary index for email lookups, hash-chain verified
- **Checkpoint-based recovery** -- fast open via backward scan, no full file scan needed
- **ULID block IDs** -- 128-bit, temporally ordered, globally unique, sync-friendly
- **Active-to-Backup sync** -- one-way replication via ULID high-water marks and FolderVersion

## Documentation

| Document | Description |
|----------|-------------|
| [File Format Spec](EmailDB_FileFormat_Spec.md) | Canonical on-disk format: block layout, types, operations |
| [BTree Index](docs/BTree_Index.md) | Primary index: leaf/internal nodes, hash chains, WAL buffering |
| [Encryption](docs/Encryption.md) | AES-256-GCM, key epochs, KeyStore, key rotation |
| [Sync](docs/Sync.md) | Active-to-Backup replication, FolderVersion, actions channel |
| [Folder Listing](docs/Folder_Listing.md) | Three-tier email model, paginated folder pages, delta WAL |
| [Search](docs/Search.md) | Secondary BTree indexes, full-text search, vector embeddings |
| [Compaction](docs/Compaction.md) | Tiered compaction, dead block reclamation |
| [Storage Estimates](docs/Storage_Estimates.md) | Capacity planning tables at various scales |

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
