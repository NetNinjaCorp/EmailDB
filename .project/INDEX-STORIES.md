# Stories

| ID | Title | Status | Priority | Points | Tags | Epic | ACs | Tasks |
| -- | ----- | ------ | -------- | ------ | ---- | ---- | --- | ----- |
| [US-EMDB-1](stories/US-EMDB-1.md) | Fix async/lock concurrency issues in managers | ✅ done | must | 5 |  | [EPIC-EMDB-1](epics/EPIC-EMDB-1.md) | 5 | 5 |
| [US-EMDB-10](stories/US-EMDB-10.md) | Implement IStorageManager with two-tier storage and soft-delete | 📋 backlog | must | 5 |  | [EPIC-EMDB-4](epics/EPIC-EMDB-4.md) | 6 | 5 |
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
| [US-EMDB-7](stories/US-EMDB-7.md) | Implement ZoneTree storage provider adapters | 📦 archived | must | 8 |  | [EPIC-EMDB-3](epics/EPIC-EMDB-3.md) | 5 | 5 |
| [US-EMDB-8](stories/US-EMDB-8.md) | Implement ZoneTree embedding-based vector search integration | 📦 archived | should | 5 |  | [EPIC-EMDB-3](epics/EPIC-EMDB-3.md) | 5 | 5 |
| [US-EMDB-9](stories/US-EMDB-9.md) | Implement EmailManager with two-tier storage (metadata + content blocks) | 📋 backlog | must | 8 |  | [EPIC-EMDB-4](epics/EPIC-EMDB-4.md) | 7 | 6 |
