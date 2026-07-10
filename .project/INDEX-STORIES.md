# Stories

| ID | Title | Status | Priority | Points | Tags | Epic | ACs | Tasks |
| -- | ----- | ------ | -------- | ------ | ---- | ---- | --- | ----- |
| [US-EMDB-1](stories/US-EMDB-1.md) | Fix async/lock concurrency issues in managers | ✅ done | must | 5 |  | [EPIC-EMDB-1](epics/EPIC-EMDB-1.md) | 5 | 5 |
| [US-EMDB-10](stories/US-EMDB-10.md) | Implement IStorageManager with two-tier storage and soft-delete | 📋 backlog | must | 5 |  | [EPIC-EMDB-4](epics/EPIC-EMDB-4.md) | 6 | 5 |
| [US-EMDB-100](stories/US-EMDB-100.md) | KeyStore sync ordering protocol | 📋 backlog | could | 3 | v3, sync, keystore | [EPIC-EMDB-21](epics/EPIC-EMDB-21.md) | 3 | 4 |
| [US-EMDB-101](stories/US-EMDB-101.md) | Delete v1 code paths | 📋 backlog | must | 5 | v3, cleanup | [EPIC-EMDB-22](epics/EPIC-EMDB-22.md) | 4 | 5 |
| [US-EMDB-102](stories/US-EMDB-102.md) | Test suite migration to v3 | 📋 backlog | must | 8 | v3, testing, migration | [EPIC-EMDB-22](epics/EPIC-EMDB-22.md) | 4 | 6 |
| [US-EMDB-103](stories/US-EMDB-103.md) | Solution hygiene | 📋 backlog | should | 3 | v3, cleanup, hygiene | [EPIC-EMDB-22](epics/EPIC-EMDB-22.md) | 3 | 4 |
| [US-EMDB-104](stories/US-EMDB-104.md) | One-pass amortized bulk load for PutBatch | ✅ done | should | 5 | v3, btree, performance, write-amplification | [EPIC-EMDB-13](epics/EPIC-EMDB-13.md) | 4 | 5 |
| [US-EMDB-11](stories/US-EMDB-11.md) | Implement MaintenanceManager compaction and cleanup | 📋 backlog | should | 5 |  | [EPIC-EMDB-5](epics/EPIC-EMDB-5.md) | 5 | 5 |
| [US-EMDB-12](stories/US-EMDB-12.md) | Implement WAL-based crash recovery | 📋 backlog | should | 8 |  | [EPIC-EMDB-5](epics/EPIC-EMDB-5.md) | 5 | 5 |
| [US-EMDB-13](stories/US-EMDB-13.md) | Fix broken test references and build test infrastructure | 📋 backlog | must | 5 |  | [EPIC-EMDB-6](epics/EPIC-EMDB-6.md) | 5 | 5 |
| [US-EMDB-14](stories/US-EMDB-14.md) | Add integration tests for end-to-end email workflows | 📋 backlog | should | 5 |  | [EPIC-EMDB-6](epics/EPIC-EMDB-6.md) | 6 | 5 |
| [US-EMDB-15](stories/US-EMDB-15.md) | Expand benchmark suite for serialization, block I/O, and SQLite comparison | 📋 backlog | could | 3 |  | [EPIC-EMDB-6](epics/EPIC-EMDB-6.md) | 4 | 4 |
| [US-EMDB-16](stories/US-EMDB-16.md) | Choose and integrate embedding model | 📋 backlog | must | 5 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 6 | 6 |
| [US-EMDB-17](stories/US-EMDB-17.md) | Implement .vec sidecar file format for vector storage | 📋 backlog | must | 8 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 6 | 8 |
| [US-EMDB-18](stories/US-EMDB-18.md) | Implement HNSW search with phased scaling path | 📋 backlog | must | 8 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 6 | 6 |
| [US-EMDB-19](stories/US-EMDB-19.md) | Auto-index emails on insert and maintain index consistency | 📋 backlog | must | 5 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 6 | 6 |
| [US-EMDB-2](stories/US-EMDB-2.md) | Fix silent error handling in CacheManager | ✅ done | must | 3 |  | [EPIC-EMDB-1](epics/EPIC-EMDB-1.md) | 3 | 3 |
| [US-EMDB-20](stories/US-EMDB-20.md) | Benchmark embedding search vs SQLite FTS5 | 📋 backlog | should | 3 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 4 | 4 |
| [US-EMDB-21](stories/US-EMDB-21.md) | Fix RawBlockManager footer int32/int64 read/write mismatch | ✅ done | must | 2 |  | [EPIC-EMDB-1](epics/EPIC-EMDB-1.md) | 4 | 4 |
| [US-EMDB-22](stories/US-EMDB-22.md) | Fix CacheManager.InitializeNewFile corrupting RawBlockManager position | ✅ done | must | 3 |  | [EPIC-EMDB-1](epics/EPIC-EMDB-1.md) | 4 | 4 |
| [US-EMDB-23](stories/US-EMDB-23.md) | Implement SQ8 scalar quantization for HNSW (Phase 2) | 📋 backlog | should | 5 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 6 | 6 |
| [US-EMDB-24](stories/US-EMDB-24.md) | Design email text preparation pipeline for embedding | 📋 backlog | must | 3 |  | [EPIC-EMDB-7](epics/EPIC-EMDB-7.md) | 5 | 5 |
| [US-EMDB-25](stories/US-EMDB-25.md) | Define B+-tree block types, node structures, and binary serialization | ✅ done | must | 5 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 7 | 7 |
| [US-EMDB-26](stories/US-EMDB-26.md) | Implement B+-tree insert with copy-on-write node splitting | ✅ done | must | 8 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 7 | 7 |
| [US-EMDB-27](stories/US-EMDB-27.md) | Implement B+-tree lookup and range query | ✅ done | must | 5 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 7 | 7 |
| [US-EMDB-28](stories/US-EMDB-28.md) | Implement B+-tree delete with tombstone and merge | ✅ done | must | 5 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 7 | 7 |
| [US-EMDB-29](stories/US-EMDB-29.md) | Implement BLAKE3 hash chaining and Merkle integrity verification | ✅ done | must | 8 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 9 | 9 |
| [US-EMDB-3](stories/US-EMDB-3.md) | Fix RawBlockManager async void and resource safety | ✅ done | must | 3 |  | [EPIC-EMDB-1](epics/EPIC-EMDB-1.md) | 4 | 4 |
| [US-EMDB-30](stories/US-EMDB-30.md) | Implement WAL-buffered writes with batch flush to B+-tree | ✅ done | must | 8 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 10 | 9 |
| [US-EMDB-31](stories/US-EMDB-31.md) | Implement IndexRoot management and crash recovery | ✅ done | must | 8 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 9 | 8 |
| [US-EMDB-32](stories/US-EMDB-32.md) | Implement B+-tree compaction (live tree rewrite) | ✅ done | should | 5 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 8 | 7 |
| [US-EMDB-33](stories/US-EMDB-33.md) | Integrate B+-tree index with EmailManager and CacheManager | ✅ done | must | 8 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 8 | 8 |
| [US-EMDB-34](stories/US-EMDB-34.md) | B+-tree performance benchmarks and stress testing | ✅ done | should | 5 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 8 | 8 |
| [US-EMDB-35](stories/US-EMDB-35.md) | WAL region model changes and RawBlockManager raw byte I/O | 📋 backlog | must | 5 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 8 | 8 |
| [US-EMDB-36](stories/US-EMDB-36.md) | Reserve 16MB WAL region during file initialization | 📋 backlog | must | 3 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 8 | 8 |
| [US-EMDB-37](stories/US-EMDB-37.md) | Implement BTreeIndex.BulkInsertAsync for optimal leaf packing | 📋 backlog | must | 8 |  | [EPIC-EMDB-8](epics/EPIC-EMDB-8.md) | 8 | 8 |
| [US-EMDB-38](stories/US-EMDB-38.md) | Define IBlockEncryptionProvider interface and NullBlockEncryptionProvider | ✅ done | must | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 4 | 4 |
| [US-EMDB-39](stories/US-EMDB-39.md) | Implement AesGcmBlockEncryptionProvider | ✅ done | must | 5 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 6 | 6 |
| [US-EMDB-4](stories/US-EMDB-4.md) | Consolidate Protobuf library choice and remove duplicates | ✅ done | must | 5 |  | [EPIC-EMDB-2](epics/EPIC-EMDB-2.md) | 4 | 4 |
| [US-EMDB-40](stories/US-EMDB-40.md) | Implement EncryptionPolicy for block type filtering | ✅ done | must | 2 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 4 | 4 |
| [US-EMDB-41](stories/US-EMDB-41.md) | Implement KeyDerivation with Argon2id and key file support | ✅ done | must | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 5 | 5 |
| [US-EMDB-42](stories/US-EMDB-42.md) | Implement EncryptionHeader for file-level crypto metadata | ✅ done | must | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 5 | 5 |
| [US-EMDB-43](stories/US-EMDB-43.md) | Integrate encryption into CacheManager read/write paths | ✅ done | must | 5 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 6 | 6 |
| [US-EMDB-44](stories/US-EMDB-44.md) | Integrate encryption into BTreeIndex read/write paths | ✅ done | must | 5 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 6 | 5 |
| [US-EMDB-45](stories/US-EMDB-45.md) | Integrate encryption into BTreeWALManager | ✅ done | should | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 5 | 4 |
| [US-EMDB-46](stories/US-EMDB-46.md) | Optional re-encryption during compaction with DEK pruning | ✅ done | should | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 7 | 7 |
| [US-EMDB-47](stories/US-EMDB-47.md) | Encryption documentation and ADR | 📋 backlog | should | 2 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 4 | 4 |
| [US-EMDB-48](stories/US-EMDB-48.md) | Encryption performance benchmarks | 📋 backlog | could | 2 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 4 | 4 |
| [US-EMDB-49](stories/US-EMDB-49.md) | Update Block model and RawBlockManager for BLAKE3-128 checksums | ✅ done | must | 5 |  | [EPIC-EMDB-10](epics/EPIC-EMDB-10.md) | 9 | 9 |
| [US-EMDB-5](stories/US-EMDB-5.md) | Implement IBlockContentSerializer with Protobuf adapter | ✅ done | must | 5 |  | [EPIC-EMDB-2](epics/EPIC-EMDB-2.md) | 5 | 5 |
| [US-EMDB-50](stories/US-EMDB-50.md) | Update RawBlockManager unit tests for BLAKE3-128 checksums | ✅ done | must | 5 |  | [EPIC-EMDB-10](epics/EPIC-EMDB-10.md) | 6 | 6 |
| [US-EMDB-51](stories/US-EMDB-51.md) | Update benchmark project for BLAKE3-128 checksums | ✅ done | should | 2 |  | [EPIC-EMDB-10](epics/EPIC-EMDB-10.md) | 5 | 5 |
| [US-EMDB-52](stories/US-EMDB-52.md) | Remove Crc32.NET dependency and add ADR for BLAKE3-128 migration | ✅ done | must | 3 |  | [EPIC-EMDB-10](epics/EPIC-EMDB-10.md) | 9 | 9 |
| [US-EMDB-53](stories/US-EMDB-53.md) | Implement Key Store Block and KeyStoreManager | ✅ done | must | 5 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 7 | 7 |
| [US-EMDB-54](stories/US-EMDB-54.md) | Update Block Flags for Key Epoch encoding | ✅ done | must | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 6 | 6 |
| [US-EMDB-55](stories/US-EMDB-55.md) | Implement KeyWrappingEncryptionProvider | ✅ done | must | 5 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 7 | 7 |
| [US-EMDB-56](stories/US-EMDB-56.md) | Implement password change via KEK re-wrap | ✅ done | must | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 8 | 8 |
| [US-EMDB-57](stories/US-EMDB-57.md) | Implement key rotation without re-encryption | ✅ done | must | 3 |  | [EPIC-EMDB-9](epics/EPIC-EMDB-9.md) | 8 | 8 |
| [US-EMDB-58](stories/US-EMDB-58.md) | Define EmailMetadata block type and model | 📋 backlog | must | 3 |  | [EPIC-EMDB-11](epics/EPIC-EMDB-11.md) | 6 | 6 |
| [US-EMDB-59](stories/US-EMDB-59.md) | Implement two-tier email write flow (content block + metadata block + BTree insert) | 📋 backlog | must | 5 |  | [EPIC-EMDB-11](epics/EPIC-EMDB-11.md) | 7 | 7 |
| [US-EMDB-6](stories/US-EMDB-6.md) | Decide Cap'n Proto layer future and clean up | ✅ done | could | 3 |  | [EPIC-EMDB-2](epics/EPIC-EMDB-2.md) | 4 | 4 |
| [US-EMDB-60](stories/US-EMDB-60.md) | Implement two-tier email read flow (metadata-only listing + lazy content load) | 📋 backlog | must | 5 |  | [EPIC-EMDB-11](epics/EPIC-EMDB-11.md) | 6 | 6 |
| [US-EMDB-61](stories/US-EMDB-61.md) | Implement soft-delete via Dead folder and optional hard delete | 📋 backlog | must | 3 |  | [EPIC-EMDB-11](epics/EPIC-EMDB-11.md) | 7 | 7 |
| [US-EMDB-62](stories/US-EMDB-62.md) | Fix FolderManager serialization for Protobuf compatibility | 📋 backlog | must | 5 |  | [EPIC-EMDB-11](epics/EPIC-EMDB-11.md) | 6 | 6 |
| [US-EMDB-63](stories/US-EMDB-63.md) | Superblock manager (dual-slot, feature flags, CleanShutdown) | ✅ done | must | 8 | v3, superblock | [EPIC-EMDB-12](epics/EPIC-EMDB-12.md) | 5 | 8 |
| [US-EMDB-64](stories/US-EMDB-64.md) | v3 block writer and reader (ULID, checksums, length sanity) | ✅ done | must | 8 | v3, blocks, format | [EPIC-EMDB-12](epics/EPIC-EMDB-12.md) | 6 | 10 |
| [US-EMDB-65](stories/US-EMDB-65.md) | Single-writer lock and fsync discipline | ✅ done | must | 5 | v3, concurrency, durability | [EPIC-EMDB-12](epics/EPIC-EMDB-12.md) | 4 | 7 |
| [US-EMDB-66](stories/US-EMDB-66.md) | Runtime block map and scan fallback | ✅ done | must | 5 | v3, recovery, scan | [EPIC-EMDB-12](epics/EPIC-EMDB-12.md) | 4 | 7 |
| [US-EMDB-67](stories/US-EMDB-67.md) | Generic node serialization (declared key/value sizes) | ✅ done | must | 5 | v3, btree, serialization | [EPIC-EMDB-13](epics/EPIC-EMDB-13.md) | 4 | 6 |
| [US-EMDB-68](stories/US-EMDB-68.md) | COW insert, delete, and range scan | ✅ done | must | 13 | v3, btree, cow | [EPIC-EMDB-13](epics/EPIC-EMDB-13.md) | 5 | 8 |
| [US-EMDB-69](stories/US-EMDB-69.md) | Merkle integrity verification (path + full) | ✅ done | must | 8 | v3, btree, integrity, merkle | [EPIC-EMDB-13](epics/EPIC-EMDB-13.md) | 5 | 8 |
| [US-EMDB-7](stories/US-EMDB-7.md) | Implement ZoneTree storage provider adapters | 📦 archived | must | 8 |  | [EPIC-EMDB-3](epics/EPIC-EMDB-3.md) | 5 | 5 |
| [US-EMDB-70](stories/US-EMDB-70.md) | IndexRoot and WAL-buffered flush | ✅ done | must | 5 | v3, btree, wal | [EPIC-EMDB-13](epics/EPIC-EMDB-13.md) | 4 | 6 |
| [US-EMDB-71](stories/US-EMDB-71.md) | BlockLocationIndex (indirection table) | ✅ done | must | 8 | v3, location-index, recovery | [EPIC-EMDB-14](epics/EPIC-EMDB-14.md) | 5 | 8 |
| [US-EMDB-72](stories/US-EMDB-72.md) | Checkpoint writer and reader | ✅ done | must | 5 | v3, checkpoint, commit | [EPIC-EMDB-14](epics/EPIC-EMDB-14.md) | 4 | 6 |
| [US-EMDB-73](stories/US-EMDB-73.md) | WAL blocks with checkpoint replay fence | ✅ done | must | 5 | v3, wal, recovery | [EPIC-EMDB-14](epics/EPIC-EMDB-14.md) | 4 | 6 |
| [US-EMDB-74](stories/US-EMDB-74.md) | Open protocol and crash recovery | ✅ done | must | 8 | v3, recovery, open | [EPIC-EMDB-14](epics/EPIC-EMDB-14.md) | 5 | 8 |
| [US-EMDB-75](stories/US-EMDB-75.md) | Corruption-handling contract implementation | ✅ done | must | 8 | v3, corruption, testing | [EPIC-EMDB-14](epics/EPIC-EMDB-14.md) | 4 | 7 |
| [US-EMDB-76](stories/US-EMDB-76.md) | Encryption bootstrap (superblock to provider) | ✅ done | must | 8 | v3, encryption, bootstrap | [EPIC-EMDB-15](epics/EPIC-EMDB-15.md) | 5 | 8 |
| [US-EMDB-77](stories/US-EMDB-77.md) | AES-GCM v3 provider (random nonces, AAD, zeroization) | ✅ done | must | 5 | v3, encryption, aes-gcm | [EPIC-EMDB-15](epics/EPIC-EMDB-15.md) | 4 | 6 |
| [US-EMDB-78](stories/US-EMDB-78.md) | Policy-driven encryption on the write/read path | ✅ done | must | 5 | v3, encryption, policy | [EPIC-EMDB-15](epics/EPIC-EMDB-15.md) | 4 | 6 |
| [US-EMDB-79](stories/US-EMDB-79.md) | Password change and key rotation | ✅ done | must | 5 | v3, encryption, rotation | [EPIC-EMDB-15](epics/EPIC-EMDB-15.md) | 5 | 7 |
| [US-EMDB-8](stories/US-EMDB-8.md) | Implement ZoneTree embedding-based vector search integration | 📦 archived | should | 5 |  | [EPIC-EMDB-3](epics/EPIC-EMDB-3.md) | 5 | 5 |
| [US-EMDB-80](stories/US-EMDB-80.md) | Tier 2/3 email blocks (EmailMetadata + EmailContent) | ✅ done | must | 5 | v3, email, tiers | [EPIC-EMDB-16](epics/EPIC-EMDB-16.md) | 4 | 6 |
| [US-EMDB-81](stories/US-EMDB-81.md) | FolderPage and FolderPageDirectory | ✅ done | must | 8 | v3, folders, pages | [EPIC-EMDB-16](epics/EPIC-EMDB-16.md) | 5 | 7 |
| [US-EMDB-82](stories/US-EMDB-82.md) | FolderDeltaLog chain and compile | ✅ done | must | 8 | v3, folders, delta | [EPIC-EMDB-16](epics/EPIC-EMDB-16.md) | 5 | 8 |
| [US-EMDB-83](stories/US-EMDB-83.md) | Tier-2 page regeneration | ✅ done | should | 3 | v3, folders, recovery | [EPIC-EMDB-16](epics/EPIC-EMDB-16.md) | 3 | 4 |
| [US-EMDB-84](stories/US-EMDB-84.md) | Lifecycle: create, open, close | ✅ done | must | 8 | v3, api, lifecycle | [EPIC-EMDB-17](epics/EPIC-EMDB-17.md) | 4 | 7 |
| [US-EMDB-85](stories/US-EMDB-85.md) | AddEmail pipeline | 📋 backlog | must | 8 | v3, api, write-path | [EPIC-EMDB-17](epics/EPIC-EMDB-17.md) | 4 | 6 |
| [US-EMDB-86](stories/US-EMDB-86.md) | GetEmail and open-email read path | 📋 backlog | must | 5 | v3, api, read-path | [EPIC-EMDB-17](epics/EPIC-EMDB-17.md) | 4 | 5 |
| [US-EMDB-87](stories/US-EMDB-87.md) | Move, delete, flag, and list operations | 📋 backlog | must | 5 | v3, api, operations | [EPIC-EMDB-17](epics/EPIC-EMDB-17.md) | 4 | 6 |
| [US-EMDB-88](stories/US-EMDB-88.md) | Dead-block accounting and compaction triggers | 📋 backlog | should | 5 | v3, compaction, accounting | [EPIC-EMDB-18](epics/EPIC-EMDB-18.md) | 4 | 6 |
| [US-EMDB-89](stories/US-EMDB-89.md) | Side-file compaction and atomic swap | 📋 backlog | must | 8 | v3, compaction, swap | [EPIC-EMDB-18](epics/EPIC-EMDB-18.md) | 5 | 8 |
| [US-EMDB-9](stories/US-EMDB-9.md) | Implement EmailManager with two-tier storage (metadata + content blocks) | 📋 backlog | must | 8 |  | [EPIC-EMDB-4](epics/EPIC-EMDB-4.md) | 7 | 6 |
| [US-EMDB-90](stories/US-EMDB-90.md) | Compaction re-encryption and DEK pruning | 📋 backlog | should | 5 | v3, compaction, encryption | [EPIC-EMDB-18](epics/EPIC-EMDB-18.md) | 4 | 6 |
| [US-EMDB-91](stories/US-EMDB-91.md) | Address trigram FTS index | 📋 backlog | should | 13 | v3, search, fts, trigram | [EPIC-EMDB-19](epics/EPIC-EMDB-19.md) | 5 | 8 |
| [US-EMDB-92](stories/US-EMDB-92.md) | Listing page scan search | 📋 backlog | should | 3 | v3, search, scan | [EPIC-EMDB-19](epics/EPIC-EMDB-19.md) | 3 | 4 |
| [US-EMDB-93](stories/US-EMDB-93.md) | Date BTree secondary index | 📋 backlog | should | 5 | v3, search, date-index | [EPIC-EMDB-19](epics/EPIC-EMDB-19.md) | 4 | 5 |
| [US-EMDB-94](stories/US-EMDB-94.md) | Query planner | 📋 backlog | could | 5 | v3, search, planner | [EPIC-EMDB-19](epics/EPIC-EMDB-19.md) | 4 | 5 |
| [US-EMDB-95](stories/US-EMDB-95.md) | .emdb.vec sidecar format | 📋 backlog | could | 8 | v3, vectors, sidecar | [EPIC-EMDB-20](epics/EPIC-EMDB-20.md) | 4 | 5 |
| [US-EMDB-96](stories/US-EMDB-96.md) | Phase-1 HNSW and embedding pipeline | 📋 backlog | could | 13 | v3, vectors, hnsw, embeddings | [EPIC-EMDB-20](epics/EPIC-EMDB-20.md) | 4 | 6 |
| [US-EMDB-97](stories/US-EMDB-97.md) | Per-folder bloom filters | 📋 backlog | could | 5 | v3, search, bloom | [EPIC-EMDB-20](epics/EPIC-EMDB-20.md) | 4 | 5 |
| [US-EMDB-98](stories/US-EMDB-98.md) | Content replication via ULID high-water mark | 📋 backlog | could | 8 | v3, sync, content | [EPIC-EMDB-21](epics/EPIC-EMDB-21.md) | 4 | 5 |
| [US-EMDB-99](stories/US-EMDB-99.md) | Folder replication via FolderVersion | 📋 backlog | could | 5 | v3, sync, folders | [EPIC-EMDB-21](epics/EPIC-EMDB-21.md) | 3 | 4 |
