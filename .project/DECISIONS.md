# EmailDB — Architecture Decisions

## ADR-001: protobuf-net for Protobuf Serialization
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: Both `Google.Protobuf` (with .proto code generation) and `protobuf-net` (with `[ProtoContract]` attributes) were referenced in `EmailDB.Format.Protobuf`. Models were duplicated in `Models/` and `Models/Blocks/`. Need to standardize on one library.  
**Options**:  
- (A) **Google.Protobuf** — requires `.proto` schema files and a code-generation build step (`Grpc.Tools`). Schema-first approach. Only used by `MetadataManager` for `MetadataPayload`.  
- (B) **protobuf-net** — attribute-based serialization with `[ProtoContract]`/`[ProtoMember]`. C#-idiomatic, no code generation. Used by all block model classes (`Block`, `BlockContent`, `MetadataContent`, `FolderContent`, `SegmentContent`, `WALContent`, etc.).  
**Decision**: **(B) protobuf-net** — attribute-based serialization.  
**Rationale**:  
- protobuf-net is already the dominant pattern across all model files  
- Attribute-based approach is more idiomatic C# — models are plain POCOs with annotations  
- No build-time code generation step required (simpler build pipeline)  
- `Google.Protobuf` was only used by `MetadataManager` for a single code-generated `MetadataPayload` class; this can be converted to a protobuf-net `[ProtoContract]` class  
- The `.proto` file (`metadata.proto`) will be removed after migration  
**Consequence**:  
- Remove `Google.Protobuf` NuGet package from `EmailDB.Format.Protobuf.csproj`  
- Convert `MetadataPayload` from code-generated class to protobuf-net `[ProtoContract]` class  
- Delete `metadata.proto`  
- Remove duplicate models from `Models/Blocks/` (keep canonical location in `Models/`)  
- All serialization uses `ProtoBuf.Serializer` APIs  

## ADR-002: Remove Cap'n Proto Layer
**Status**: Accepted  
**Date**: 2026-02-23  
**Context**: `EmailDB.Format.CapnProto` was created as a potential alternate serialization layer alongside Protobuf. After thorough analysis, the project is largely non-functional: no `.capnp` schema files exist, `BlockManager` is entirely stubbed (all methods throw `NotImplementedException`), `CacheManager` content retrieval methods return null, and no `IBlockContentSerializer` implementation exists. Only the raw I/O layer (`RawBlockManager`) works, which duplicates functionality already in `EmailDB.Format`. Meanwhile, the Protobuf layer (`EmailDB.Format.Protobuf`) is fully implemented with `ProtobufBlockContentSerializer`, complete model coverage, and working serialization.  
**Options**:  
- (A) **Complete as alternate serialization** — would require writing `.capnp` schema files for all block types, implementing a `CapnProtoBlockContentSerializer`, completing the stubbed `BlockManager` and `CacheManager` methods, and maintaining ongoing parity with the Protobuf layer.  
- (B) **Remove entirely** — delete the project and clean up solution references. Keep Protobuf as the sole serialization layer. The `IBlockContentSerializer` interface (ADR-006) allows adding alternate serializers in the future if needed.  
**Decision**: **(B) Remove entirely.**  
**Rationale**:  
- ~70% of the project is stubbed or non-functional — completing it would be significant effort with no clear benefit  
- No `.capnp` schema files exist, so there is no schema investment to preserve  
- Protobuf-net (ADR-001) is already fully implemented, tested, and production-ready  
- The pluggable `IBlockContentSerializer` interface means a Cap'n Proto serializer can be added later if a concrete need arises  
- Cap'n Proto's zero-copy advantage is negligible here — email blocks are already cached in memory by `CacheManager`, and block reads are not in the hot path  
- Maintaining a dead/broken project adds confusion for contributors and CI noise  
- The functional `RawBlockManager` in CapnProto is a near-duplicate of the one in `EmailDB.Format` — no unique logic would be lost  
**Consequence**:  
- Delete `EmailDB.Format.CapnProto/` project directory  
- Remove project reference from `EMDBTesting.sln`  
- Update `EmailDB.Testing.FileFormatBenchmark` to remove CapnProto benchmark references  
- Remove or update any test files that reference CapnProto types  
- Update architecture doc to remove "Cap'n Proto (alternate)" from serialization section  
- Tracked in sibling tasks US-EMDB-6-3 (removal) and US-EMDB-6-4 (verify no broken code)  

## ADR-003: Block Format — Established
**Status**: Accepted (checksum portion superseded by ADR-014)
**Context**: Binary block format. Original design used 60 bytes fixed overhead with CRC32 checksums; checksum fields upgraded to BLAKE3-128 (16 bytes each) per ADR-014, and footer expanded, bringing total fixed overhead to 91 bytes.
**Decision**: Header (37 bytes) + checksum (16B BLAKE3-128) + variable payload + checksum (16B BLAKE3-128) + Footer (22 bytes). Magic numbers for validation. Append-only writes. Block ID is a ULID (sortable, collision-free).  

## ADR-004: Layered Architecture — Established
**Status**: Accepted  
**Decision**: RawBlockManager → CacheManager → MetadataManager → FolderManager/SegmentManager → EmailManager. Each layer has clear responsibilities. Higher layers never bypass lower layers.

## ADR-005: ZoneTree for Email Indexing
**Status**: Superseded by ADR-013  
**Date**: 2026-02-22  
**Superseded Date**: 2026-02-23  
**Decision**: ZoneTree LSM-tree for KV storage (email content) and HashedSearchEngine for full-text search. ZoneTree uses pluggable storage interface that routes through BlockManager into the EMDB file.  
**Why Superseded**: ZoneTree requires implementing 3 complex adapter interfaces (`IRandomAccessDeviceManager`, `IFileStreamProvider`, `IWriteAheadLogProvider`) totaling ~1000 lines to map its folder-of-files storage model into our single-file block store. This is a significant maintenance liability that couples us to ZoneTree's internal API. A self-built append-only B+-tree fits our block format natively and adds hash-chain integrity that ZoneTree doesn't provide. See ADR-013.

## ADR-006: Content Serialization via IBlockContentSerializer
**Status**: Accepted  
**Decision**: Pluggable serialization through IBlockContentSerializer/IPayloadEncoding interface. PayloadEncoding enum in block header indicates format used. Allows per-block format selection.

## ADR-007: EmailDB over SQLite — Decided via Benchmarks
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: SQLite + FTS5 was identified as the strongest alternative to EmailDB's custom block format. A comprehensive BenchmarkDotNet benchmark suite (`EmailDB.Benchmark.SQLite`) was built to compare both approaches head-to-head across all key operations at 1K, 10K, and 100K email scale.  
**Benchmark Results**:
| Operation | SQLite | EmailDB | Winner |
|-----------|--------|---------|--------|
| Bulk Insert (100K) | 12,373 ms | 1,260 ms | EmailDB ~10x |
| Single Insert (10K) | 10,146 ms | 146 ms | EmailDB ~69x |
| Get-by-ID (100K preloaded) | 22 us | 3 us | EmailDB ~7x |
| Folder Listing (100K) | 68,804 us | 11,089 us | EmailDB ~6x |
| Full-Text Search (100K) | 31 ms | 1,514 ms | SQLite ~49x |
| Delete (100 emails) | 86 ms | 15 ms | EmailDB ~6x |
| Move (100 emails) | 57 ms | 32 ms | EmailDB ~2x |
**Decision**: Continue with EmailDB custom block format. EmailDB wins decisively on write throughput, point lookups, folder listing, and mutations. SQLite's only advantage is full-text search via FTS5. This gap will be closed by implementing embedding-based semantic search, which provides both better performance (vector index lookup vs linear scan) and better search quality (semantic matching vs keyword matching).  
**Consequence**: New epic (EPIC-EMDB-7) created for Embedding Search to eliminate the one remaining weakness.

## ADR-008: Embedding Search over Keyword Search
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: Benchmarks showed EmailDB's linear scan search is ~49x slower than SQLite FTS5 at 100K emails. Two options: (A) Build a keyword-based FTS index within EmailDB, (B) Build embedding-based vector search.  
**Decision**: Embedding-based vector search. Rationale:
- Vector similarity lookup is O(1)/O(log n) with HNSW index, eliminating the linear scan
- Semantic matching is superior to keyword matching (e.g., "meeting notes" finds "standup summary")
- Aligns with existing architecture — embeddings stored alongside email data
**Consequence**: Embedding search scoped in EPIC-EMDB-7, decoupled from KV index.

## ADR-009: Sidecar File for Vector Index
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: At scale (10M-200M+ emails), embedding vectors + HNSW graph can reach hundreds of GB. Storing this inside the .emdb file would bloat the primary data file and couple email storage to search infrastructure. A web frontend for search would need access to the full .emdb file even though it only needs the vector index.  
**Decision**: Store embeddings and HNSW index in a **sidecar file** (`*.emdb.vec`) alongside each .emdb file. Each .emdb shard gets its own `.vec` sidecar.
**File layout**:
```
mailbox/
├── emails_001.emdb          (email data, folders, metadata — up to ~50GB)
├── emails_001.emdb.vec      (sidecar: embeddings + HNSW index)
├── emails_002.emdb          (next shard)
├── emails_002.emdb.vec      (sidecar)
└── ...
```
**Rationale**:
- **Deployment flexibility**: Web frontend only needs `.vec` files to serve search queries
- **Independent lifecycle**: Rebuild/reindex embeddings without touching email data
- **Natural sharding**: Each shard's vector index is self-contained, search fans out + merges
- **Model upgrades**: Regenerate `.vec` files when switching embedding models without email migration
- **Separation of concerns**: Email storage format doesn't need to understand vector indexing internals
**Consequence**: Breaks the "single file" spec (ADR-003 context) but the trade-off is clearly worthwhile for scalability and operational flexibility.

## ADR-010: Phased HNSW Scaling Strategy
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: Need a vector search solution that works from 100K emails up to 200M+ emails per user. No existing pure C# library handles quantization or memory-mapped HNSW. Different scales demand different strategies.  
**Decision**: Phased implementation with increasing sophistication:

| Phase | Scale | Strategy | RAM per shard | Search latency |
|-------|-------|----------|---------------|----------------|
| 1 | <1M emails | Float32 HNSW in memory | ~600 MB | <1ms |
| 2 | 1-10M | Scalar quantized (SQ8) HNSW | ~150 MB | 1-3ms |
| 3 | 10-50M | SQ8 + mmap'd vectors from .vec | ~15 MB (graph) | 3-10ms |
| 4 | 50M+ | PQ or Vamana/DiskANN | ~5 MB | 5-15ms |

**Phase 1** (MVP): Use existing C# HNSW library (HnswLite or curiosity-ai HNSW) with float32 vectors. 384-dim all-MiniLM-L6-v2 embeddings. Sufficient for <1M emails (~600MB RAM).

**Phase 2**: Build SQ8 quantization layer on top — per-dimension min/max scaling to int8, SIMD distance computation via `System.Runtime.Intrinsics`. 4x compression, <1% recall loss.

**Phase 3**: Memory-map quantized vectors from `.vec` file via `System.IO.MemoryMappedFiles`. Graph stays in managed memory, OS handles vector paging. Decouples RAM from dataset size.

**Phase 4** (if needed): Product quantization (PQ) for 16-32x compression, or port Vamana/DiskANN for true disk-native search. Alternatively P/Invoke to hnswlib or Faiss.

**Embedding model**: Start with all-MiniLM-L6-v2 (384 dims, ~80MB ONNX model). At 384 dims, 50GB .emdb shard = 5-15M emails -> ~2.2-8.6 GB vectors per shard. Comfortably within Phase 1-2 for most users.

**Key numbers at 50GB shard (~10M emails, 384-dim float32)**:
- Raw vectors: ~14.7 GB
- SQ8 vectors: ~3.7 GB
- HNSW graph: ~1.5 GB
- Total .vec sidecar (SQ8): ~5.2 GB

**Search latency targets** (end-to-end, including query embedding):
- HNSW lookup: <5ms at 100K (Phase 1)
- Query embedding: ~1-15ms (model-dependent)
- Total: <20ms end-to-end target at 100K

## ADR-011: Email Text Preparation for Embedding
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: MiniLM-L6-v2 has a 256 token limit (~200 words). Most emails exceed this. Need a defined strategy for converting raw email -> embedding-ready text.  
**Decision**:
- **Input fields**: Subject + From + body text (concatenated with separator tokens)
- **Format**: `"Subject: {subject} | From: {from} | {body_text}"`
- **HTML handling**: Strip HTML tags before embedding (plain text extraction)
- **Truncation**: Truncate to 256 tokens. Subject + From get priority (placed first), body fills remaining tokens. This ensures the most identifying information (who sent what) is always embedded even for very long emails.
- **Distance metric**: Cosine similarity. MiniLM outputs are L2-normalized, so cosine = dot product. Use dot product for SIMD efficiency.
- **Query embedding**: Same model, same tokenizer. Query text embedded at search time (~1-15ms overhead). This is added to the end-to-end latency budget.
**Alternatives considered**:
- Multi-vector (chunk long emails into multiple vectors): Better recall for long emails but 2-5x storage and complex retrieval. Defer to Phase 2+ if needed.
- Longer-context model (E5-mistral, 4096 tokens): Better for long emails but 4096 dimensions = 16x storage, much slower inference. Not justified at MVP.
**Consequence**: US-EMDB-24 implements this pipeline. See US-EMDB-16 for model integration.

## ADR-012: HNSW Persistence Strategy
**Status**: Accepted  
**Date**: 2026-02-22  
**Context**: HNSW graphs cannot be incrementally appended to a flat file — adding a new node modifies existing nodes' neighbor lists (bidirectional edge insertion). Need a strategy for persisting the index.  
**Decision**:
- **In-memory primary**: HNSW graph lives in managed memory during operation
- **Full flush on close**: Entire graph serialized to .vec sidecar on graceful shutdown
- **Periodic checkpoint**: Background flush every N inserts or T minutes to limit data loss window
- **Crash recovery**: On unclean shutdown, .vec may be stale. Detect via generation counter in .emdb metadata vs .vec header. Rebuild from .emdb if mismatched.
- **Rebuild is always safe**: .vec is derived data — can always be regenerated from .emdb source
- **Thread safety**: AsyncReaderWriterLock on the HNSW index. Multiple concurrent readers (searches), exclusive writer (inserts/deletes). Search never blocks on insert.
**Consequence**: This means search results during heavy insert may not include the very latest emails (eventual consistency within the checkpoint window). This is acceptable for email search.

## ADR-013: Self-Built Append-Only B+-Tree over ZoneTree
**Status**: Accepted  
**Date**: 2026-02-23  
**Supersedes**: ADR-005  
**Context**: The original plan (ADR-005) used ZoneTree, a C# LSM-tree library, for email KV storage and full-text search. All 6 ZoneTree adapter files in `EmailDB.Format/ZoneTree/` (~1000 lines) were written but remain 100% commented out. The adapters would implement `IRandomAccessDeviceManager`, `IFileStreamProvider`, and `IWriteAheadLogProvider` to map ZoneTree's folder-of-files storage model into our single-file append-only block store. Additionally, ZoneTree.FullTextSearch provides token-hashed keyword search, not semantic/embedding search — and the embedding search decision (ADR-008) already committed us to HNSW vector search in a separate sidecar file.

**Options**:
- (A) **Complete ZoneTree integration** — uncomment and finish the 6 adapter files. Implement 3 complex interfaces mapping ZoneTree's virtual filesystem to block storage. Use ZoneTree for KV operations and optionally its FullTextSearch extension.
- (B) **Self-built append-only B+-tree** — implement a CouchDB-style copy-on-write B+-tree that stores nodes directly as .emdb blocks. Add BLAKE3 hash chaining for tamper detection. WAL-buffered writes for performance. No external KV dependency.

**Decision**: **(B) Self-built append-only B+-tree with BLAKE3 hash chaining.**

**Rationale**:
1. **Architectural fit**: Our block format is append-only by design. A copy-on-write B+-tree maps naturally — each mutated node is simply a new block appended to the file. ZoneTree's LSM-tree model assumes mutable segment files and a directory structure, requiring a complex abstraction layer to reconcile.
2. **Maintenance burden eliminated**: The 3 ZoneTree adapter interfaces represent ~1000 lines of code that must track ZoneTree's internal API across version updates. A self-built index has zero external coupling.
3. **Hash-chained integrity**: BLAKE3 hash chaining (each block links to the hash of the previous block) and Merkle tree verification (internal nodes carry child hashes) provide tamper detection from the root hash down to any leaf. ZoneTree offers no equivalent integrity guarantee.
4. **Dependency reduction**: Removes the `ZoneTree.FullTextSearch` NuGet dependency. Embedding search (EPIC-EMDB-7) replaces keyword search entirely, making ZoneTree.FullTextSearch redundant.
5. **Sufficient scale**: An append-only B+-tree with 4096-byte nodes handles 13.6M+ entries at height 4 (55 fan-out with Merkle hashes, 82 entries per leaf). Point lookups require at most 4 block reads, with upper levels cached. This exceeds the 10M email target.
6. **No scan-on-startup**: Unlike a flat offset map rebuilt from file scanning, the B+-tree root offset is stored in an IndexRoot block at the end of the file. Recovery is O(WAL_size), not O(file_size).

**Technical Design**:

### Block Types
```csharp
BTreeLeaf     = 6,   // Leaf node: sorted key-value entries
BTreeInternal = 7,   // Internal node: separator keys + child offsets + child hashes
IndexRoot     = 8,   // Root pointer + chain hash + entry count + tree height
EmailContent  = 9    // Email data block (distinct from index blocks)
```

### Node Binary Layout (custom serialization, NOT protobuf)
B+-tree nodes use fixed-size fields (32B keys, 16B values) making protobuf's variable-length encoding pure overhead. Custom `BinaryWriter`/`BinaryReader` serialization is used for zero-overhead packing.

**Leaf node** (within 4036B usable payload after 91B block overhead):
```
NodeType(1) | Version(1) | EntryCount(2)
NodeContentHash(32)    — BLAKE3 of this node's entries
PrevChainHash(32)      — BLAKE3 of previous block written
Entries[]: EntryCount * 48B
  Key(32B EmailHashedID) + Value(16B BlockLocation: offset + length)
```
Max 82 entries per leaf at 4036 usable bytes.

**Internal node**:
```
NodeType(1) | Version(1) | KeyCount(2)
NodeContentHash(32) | PrevChainHash(32)
Keys[]: KeyCount * 32B
ChildOffsets[]: (KeyCount+1) * 8B
ChildHashes[]: (KeyCount+1) * 32B   — Merkle verification
```
Max 55 children per internal node (with Merkle hashes).

**IndexRoot payload** (58 bytes):
```
RootNodeBlockOffset(8) | EntryCount(8) | TreeHeight(2)
RootNodeHash(32)       — BLAKE3 of root node content
Sequence(8)            — monotonic counter (highest = latest during recovery)
```

No backward hash chain. Compaction discards the chain anyway, so maintaining it is wasted effort. The Checkpoint block points directly to the authoritative IndexRoot; the sequence number is only a fallback for recovery when no valid Checkpoint exists.

### Capacity
| Tree Height | Max Entries | Use Case |
|-------------|-------------|----------|
| 1 | 82 | Tiny mailbox |
| 2 | ~4,500 | Small mailbox |
| 3 | ~248K | Medium mailbox |
| 4 | **~13.6M** | Large mailbox (target) |

### Hash Chain Design
- **Algorithm**: BLAKE3-256 via [Blake3.NET](https://www.nuget.org/packages/Blake3) by xoofx. SIMD-accelerated (AVX2/AVX-512), 4-10x faster than SHA-256. 32-byte output.
- **Sequential chain**: Every B+-tree block includes `PrevChainHash` = BLAKE3(previous block). Creates a tamper-evident chain. Modifying any block breaks the chain at its successor.
- **Merkle tree**: Internal nodes carry `ChildHashes[]` = BLAKE3 of each child node's content. The root `NodeContentHash` represents the integrity of the entire index. Enables per-path verification without reading the whole tree.
- **IndexRoot chain**: `PreviousRootHash` links IndexRoot blocks across flush boundaries. Enables quick verification of the flush history.
- **Verification modes**:
  - **Quick**: Walk IndexRoot chain only — O(flush_count)
  - **Standard**: Verify Merkle path from root to a specific leaf — O(tree_height)
  - **Full**: Traverse entire tree verifying all hashes — O(total_nodes)
- **Separate from content hashing**: SHA-3 remains the algorithm for `EmailHashedID` (content identity). BLAKE3 is used exclusively for structural integrity (chain + Merkle). Different concerns, different algorithms.

### Write Path
1. Insert/delete buffered in WAL block (existing `BlockType.WAL`)
2. Flush triggered by count threshold (default 100), time threshold (default 5s), or explicit call
3. Batch flush: sort WAL entries by key, apply to B+-tree, write all modified nodes as new blocks
4. `fsync` — ensure all nodes on disk
5. Write IndexRoot block with new root offset + chain hash
6. `fsync` — ensure root pointer durable
7. Clear WAL

Write amplification: batch of 100 inserts touching ~10 leaves = ~15 node writes instead of 400 (4 per insert * 100).

### Crash Recovery
1. Scan backwards from EOF for last valid IndexRoot block (verify CRC + BLAKE3 chain)
2. Load root node from `IndexRoot.RootNodeBlockOffset`
3. Verify `IndexRoot.RootNodeHash` matches loaded root (optional integrity check)
4. Scan for WAL blocks written after last IndexRoot
5. Replay WAL entries (re-flush to tree)
6. Write new IndexRoot if replay occurred

**Invariant**: After recovery, the tree represents a consistent state at some committed flush boundary plus any replayed WAL entries.

### CouchDB-Style Constraints
- **No sibling pointers**: Leaf nodes do not link to their neighbors. Range queries navigate by backtracking to the parent to find the next child. This avoids cascade rewrites when a sibling splits.
- **No in-place modification**: Every mutation produces new node copies from leaf to root. Unchanged subtrees are shared between old and new tree versions.
- **Compaction**: Walk the live tree from the current root, copy only reachable nodes to a new file. Dead (superseded) nodes are not copied. New hash chain starts with genesis.

### Architecture Integration
```
IStorageManager
  └─ EmailManager
       ├─ BTreeIndex (KV lookup: EmailHashedID → BlockLocation)
       │    ├─ RawBlockManager (block I/O for B+-tree nodes)
       │    └─ CacheManager (upper node caching)
       ├─ RawBlockManager (email content block I/O)
       └─ [HNSW Vector Index — via sidecar .vec file, EPIC-EMDB-7]
```

The B+-tree index and HNSW embedding search are fully independent. Search returns `EmailHashedID`s, then the B+-tree resolves them to `BlockLocation`s. Both can be developed in parallel.

**Alternatives Considered**:
- **LSM-tree (ZoneTree model)**: Better write throughput for sequential inserts, but requires complex segment management and multiple levels of compaction. The adapter complexity to route through our block store negated the write performance benefit.
- **Hash table (linear probing)**: O(1) lookup but no range queries, no ordered iteration, and resize requires rewriting the entire table. Not suitable for append-only storage.
- **Skip list**: Good concurrent performance but poor disk locality and no natural block-aligned storage.
- **Flat offset map + scan**: Simplest approach — `Dictionary<EmailHashedID, BlockLocation>` rebuilt from full file scan on startup. Rejected because scan time at 10M blocks is unacceptable (O(file_size) recovery).

**Consequence**:
- New epic EPIC-EMDB-8 created with 10 stories, 65 story points, 80 test tasks
- EPIC-EMDB-3 (ZoneTree Integration) archived
- US-EMDB-7 (ZoneTree storage adapters) and US-EMDB-8 (ZoneTree search) archived
- `ZoneTree.FullTextSearch` NuGet dependency to be removed
- 6 commented-out files in `EmailDB.Format/ZoneTree/` to be deleted
- `Blake3` NuGet dependency to be added
- Updated layered architecture: RawBlockManager → CacheManager → BTreeIndex → EmailManager

## ADR-014: CRC32 → BLAKE3-128 Block Checksums
**Status**: Accepted
**Date**: 2026-02-24
**Supersedes**: ADR-003 (checksum portion only)
**Context**: The original block format (ADR-003) used CRC32 (4-byte) checksums on both header and payload. As the B+-tree index (ADR-013) introduced BLAKE3-256 hash chains for structural integrity, the block-level checksums remained CRC32 — creating an inconsistency where two different hashing strategies coexisted. Additionally, the upcoming encryption epic (EPIC-EMDB-9) requires checksums computed on ciphertext to resist intentional tampering, which CRC32 cannot provide.

**Options**:
- (A) **Keep CRC32** — minimal change, sufficient for accidental bit-flip detection. 4-byte checksums, 57-byte total fixed overhead per block.
- (B) **Upgrade to BLAKE3-128** — truncated BLAKE3 hash (first 16 bytes of BLAKE3 output). 128-bit collision resistance, cryptographic integrity. 16-byte checksums, 81-byte total fixed overhead per block.
- (C) **Upgrade to full BLAKE3-256** — 32-byte checksums, 121-byte overhead. Maximum security but higher space cost.

**Decision**: **(B) BLAKE3-128 — truncated 128-bit BLAKE3 checksums.**

**Rationale**:
1. **32-bit keyspace insufficient**: CRC32's 32-bit output means a 1-in-4-billion chance of collision. At scale (millions of blocks), accidental collisions become plausible, and intentional collisions are trivial to construct.
2. **No tamper resistance**: CRC32 is not a cryptographic hash — an attacker can modify a block's payload and recompute a valid CRC32 in microseconds. This is unacceptable for the encryption epic (EPIC-EMDB-9), where checksums on ciphertext must detect intentional modification.
3. **Inconsistent with BLAKE3 hash chains**: The B+-tree index (ADR-013) already uses BLAKE3-256 for sequential chain hashes and Merkle tree verification. Using CRC32 for block checksums alongside BLAKE3 for structural integrity creates two different trust boundaries with different security properties. BLAKE3-128 unifies the hashing story.
4. **Prerequisite for encryption epic**: EPIC-EMDB-9 (Block-Level Encryption) adds AES-256-GCM payload encryption. Checksums computed on ciphertext need cryptographic integrity to detect tampering — CRC32 provides none. Upgrading checksums before adding encryption avoids a second format migration.
5. **BLAKE3 performance**: BLAKE3 is SIMD-accelerated (AVX2/AVX-512) via Blake3.NET and benchmarks at 4-10x faster than SHA-256. The overhead of computing 128-bit BLAKE3 vs CRC32 is negligible relative to disk I/O.
6. **128-bit is sufficient**: BLAKE3-128 (truncating BLAKE3 to 16 bytes) provides 2^128 collision resistance — far beyond any practical attack. Full 256-bit output would add 24 bytes of overhead per block for no practical security benefit at this layer. The B+-tree's structural integrity already uses full BLAKE3-256.

**Format Change**:
- Header checksum: 4 bytes (CRC32) → 16 bytes (BLAKE3-128)
- Payload checksum: 4 bytes (CRC32) → 16 bytes (BLAKE3-128)
- Header expanded from 36 to 37 bytes, footer expanded from 13 to 22 bytes
- Total fixed overhead per block: 57 bytes → 91 bytes
- Block layout: `Header (37B) + HeaderChecksum (16B) + Payload (variable) + PayloadChecksum (16B) + Footer (22B)`
- Checksum algorithm identifier updated in format spec

**Consequence**:
- `Force.Crc32` / `Crc32.NET` NuGet packages removed from all projects
- `RawBlockManager.ComputeChecksum` returns `byte[]` (16 bytes) instead of `uint` (4 bytes)
- `Block.HeaderChecksum` and `Block.PayloadChecksum` properties changed from `uint` to `byte[]`
- `HeaderChecksumSize` and `PayloadChecksumSize` constants changed from 4 to 16
- `TotalFixedOverhead` constant changed from 57 to 91
- Format spec (`EmailDB_FileFormat_Spec.md`) updated to reflect 16-byte checksums and 81-byte overhead
- ADR-003 updated to note checksum portion superseded by ADR-014
- Tracked in EPIC-EMDB-10 (BLAKE3-128 Block Checksums)

## ADR-015: KEK/DEK Key-Wrapping Architecture
**Status**: Accepted
**Date**: 2026-02-24
**Context**: The initial encryption design (EPIC-EMDB-9, stories 38-42) implemented a single master key model — one AES-256-GCM key derived from the user's password encrypts all blocks. This design has three critical problems: (1) **Password change requires re-encrypting every block** — changing the user's password means deriving a new key and re-encrypting the entire database, which is O(file_size) and impractical for large mailboxes. (2) **Key compromise exposes all data** — a single key means a single point of failure; if the key is ever leaked, every block past and future is compromised. (3) **No forward secrecy** — there is no mechanism to limit the blast radius of a key breach to a time window.

The original design intent was always a key-wrapping architecture where old blocks stay encrypted as-is and only the key management layer changes on password rotation. This ADR formalizes that design.

**Options**:
- (A) **Single master key** (current implementation) — one key derived from password encrypts all blocks. Password change or key rotation requires re-encrypting all blocks. Simple but expensive and insecure.
- (B) **KEK/DEK key-wrapping** — two-tier key hierarchy. A Key Encryption Key (KEK) derived from the password encrypts a Key Store Block containing per-epoch Data Encryption Keys (DEKs). Each block references its DEK by epoch. Password change re-encrypts only the key store. Key rotation adds a new DEK without touching old blocks.
- (C) **Per-block unique keys** — every block gets its own random key stored in a key table. Maximum isolation but large key store and no practical benefit over epoch-based rotation.

**Decision**: **(B) KEK/DEK key-wrapping with epoch-based rotation.**

**Design**:

### Key Hierarchy
```
User Password
    │
    ▼ (Argon2id: 64MB memory, 3 iterations, 4 parallelism)
Key Encryption Key (KEK) — 32 bytes
    │
    ▼ (AES-256-GCM encrypt/decrypt)
Key Store Block (BlockType.KeyStore)
    │
    ├─ Epoch 0: DEK₀ (32 bytes) — created at file init
    ├─ Epoch 1: DEK₁ (32 bytes) — first rotation
    ├─ Epoch 2: DEK₂ (32 bytes) — second rotation
    │   ...
    └─ ActiveEpoch: 2
         │
         ▼ (AES-256-GCM per-block encryption)
     Data Blocks (email content, folders, WAL, etc.)
```

### Flags Byte Layout
The existing 1-byte `Flags` field in the block header is repurposed:
```
Bit 0:    Encrypted flag (1 = encrypted, 0 = plaintext)
Bits 1-7: Key Epoch (0-127, meaningful only when Encrypted = 1)
```
- `FlagAlgorithm` (bit 1, 0x02) is removed — the encryption algorithm is file-global and stored in the EncryptionHeader
- Key epoch range: 0-127 (128 epochs per compaction cycle)
- At monthly rotation, this supports 10+ years before wrapping
- Compaction resets all blocks to the current epoch, resetting the counter

### Key Store Block
A new `BlockType.KeyStore` block stored in the file:
```
KeyStoreVersion(1)       — format version for future upgrades
ActiveEpoch(1)           — which epoch new blocks should use
EntryCount(1)            — number of DEK entries
Entries[]:
  Epoch(1)               — epoch identifier (0-127)
  DEK(32)                — AES-256 data encryption key
  CreatedTimestamp(8)     — when this DEK was generated
  Retired(1)             — 0 = active, 1 = retired (no new blocks use it)
```
- Total per entry: 42 bytes. Max 128 entries = ~5.4 KB
- The entire key store payload is encrypted with the KEK using AES-256-GCM
- On file creation: generate DEK₀, write key store block encrypted with KEK
- On file open: derive KEK from password, decrypt key store block, load DEK table into memory

### Operations

**Password Change** — O(1), touches only the key store block:
1. Derive old KEK from old password + existing salt
2. Decrypt key store block with old KEK
3. Generate new salt
4. Derive new KEK from new password + new salt
5. Re-encrypt key store block with new KEK
6. Update EncryptionHeader (new salt, new key verification token)
7. Write updated key store block and header
8. Zero data blocks touched

**Key Rotation** — O(1), adds new DEK:
1. Generate new 32-byte random DEK
2. Mark current active epoch as retained (still needed for existing blocks)
3. Add new DEK entry with next epoch number
4. Set new epoch as active
5. Re-encrypt and write key store block
6. All future blocks encrypted with new DEK
7. All existing blocks remain encrypted with their original DEK

**Compaction (optional re-encryption)**:
1. During normal compaction (space reclamation), blocks CAN be re-encrypted with the current active DEK
2. After re-encryption, old DEKs with no remaining block references are pruned from the key store
3. This is an optimization, not a requirement — compaction works fine without re-encryption
4. Enables periodic "key cleanup" to limit the number of DEKs in the key store

### Integration with Existing Components
- **EncryptionHeader**: Unchanged — still stores salt, algorithm ID, KDF type, key verification token. The KEK is derived from password + salt as before. The header now conceptually protects the KEK, not individual block keys.
- **AesGcmBlockEncryptionProvider**: Still used as the underlying encryption primitive, but now wrapped by a `KeyWrappingEncryptionProvider` that handles DEK lookup by epoch.
- **EncryptionPolicy**: Unchanged — still determines which block types are encrypted. The key epoch is only stamped on encrypted blocks.
- **IBlockEncryptionProvider interface**: Extended to expose key epoch on encrypt (so the caller can stamp the Flags byte) and accept key epoch on decrypt (so it can look up the right DEK).

**Rationale**:
1. **O(1) password change**: Re-encrypting only the key store block (~5 KB) vs re-encrypting potentially gigabytes of data. This is the difference between milliseconds and hours for a large mailbox.
2. **Forward secrecy via rotation**: If DEK₁ is somehow compromised, only blocks encrypted with epoch 1 are exposed. Blocks from epoch 0 and epoch 2+ remain secure. Regular rotation limits the blast radius.
3. **No re-encryption of old blocks**: Old blocks stay encrypted with their original DEK. The key store retains all DEKs needed to decrypt them. This is the core design principle — encrypt once, never re-encrypt unless you choose to during compaction.
4. **Standard pattern**: This is how LUKS, BitLocker, and most enterprise encryption systems work. Well-understood security properties.
5. **Minimal format change**: The Flags byte already exists in every block header. Repurposing bits 1-7 for key epoch adds zero bytes of overhead. The key store is a single additional block.

**Consequence**:
- New `BlockType.KeyStore` added to enum
- `Block.FlagAlgorithm` removed; bits 1-7 of Flags repurposed for key epoch
- `Block.KeyEpoch` property added: `(Flags >> 1) & 0x7F`
- New `KeyStoreManager` class for reading/writing the key store block
- New `KeyWrappingEncryptionProvider` wraps `AesGcmBlockEncryptionProvider` with DEK lookup
- US-EMDB-46 (key rotation via compaction) rewritten — compaction re-encryption is optional, not the primary rotation mechanism
- New stories added: Key Store Block, Block Flags update, KeyWrappingEncryptionProvider, Password Change, Key Rotation
- Integration stories (US-EMDB-43, 44, 45) updated to use `KeyWrappingEncryptionProvider`
- Tracked in EPIC-EMDB-9 (Block-Level Encryption)

## ADR-016: v3 File Format — Canonical Build Target
**Status**: Accepted
**Date**: 2026-07-01
**Supersedes**: v2 format spec (and reconciles ADR-003/013/014/15 details with the audit findings)
**Context**: A full doc-vs-code audit (2026-07-01) found three mutually inconsistent format descriptions: the as-built v1 code (36B header/84B overhead, int64 BlockIds, offset-addressed BTree), the v2 design spec (48B/96B, ULID, Checkpoint — never implemented), and a stale intermediate draft in ARCHITECTURE.md. It also found design holes none of them addressed: no torn-write-safe file header (the in-place offset-0 header overwrite is a live corruption bug), no cryptographic binding between block headers and encrypted payloads, KDF parameters hardcoded rather than stored in-file, a nonce scheme unsafe under re-encrypting compaction, and integrity hashes that are written but never verified.

**Decision**: Define **v3** as the single canonical format (rewritten `EmailDB_FileFormat_Spec.md`) and build to it. Key points, relative to v2:
1. **Dual-slot superblock** (2 × 4 KB at offset 0; alternate writes, sequence + checksum, highest valid wins) — atomically solves password change, O(1) fast-open (Checkpoint pointer), and replaces the in-place Metadata-block-at-offset-0 hack.
2. **Feature flags** (Compat / ReadOnlyCompat / Incompat masks, ext4-style) + **FileId ULID** + ShardIndex for identity, sync, and forward evolution.
3. **ULID BlockIds everywhere; no persisted offsets as durable pointers.** BTree leaves store Key(32)+BlockId(16); internal nodes 48 keys/49 children with ChildBlockId+ChildHash. Compaction never rewrites index content.
4. **AES-GCM AAD binding**: FileId‖BlockId‖BlockType‖KeyEpoch — prevents ciphertext transplant attacks. Nonce is 12 fully random bytes (ULID-derived nonce rejected: re-encryption collision risk). KdfParams (16B) stored in superblock — files are self-describing.
5. **KeyEpoch is a dedicated 2-byte header field** (0–65535), not Flags bits (127 was too tight with re-encrypting compaction).
6. **Header Timestamp field removed** — the ULID's 48-bit ms timestamp is the timestamp. Header: 48B; total fixed overhead 96B.
7. **Pure append-only**: FolderDeltaLog becomes chained appended blocks (v2's fixed rewrite region dropped); the superblock is the only in-place structure. WAL is standard blocks carrying CheckpointBlockId (replay fence).
8. **PrevChainHash removed** from BTree nodes; Merkle verification (ChildHash / RootHash) is REQUIRED at read time — write-only hashing is non-conforming.
9. **Checkpoint gains a generic SecondaryIndexes table** ({IndexKind, BlockId, Offset}) for date BTree / FTS / bloom roots.
10. **Corruption-handling contract**: defined required behavior for every verification failure (superblock, checksum, GCM tag, Merkle, torn checkpoint, torn tail).

**Update (2026-07-01, greenfield hardening)**: With no real files in existence, backward compatibility was dropped entirely and the spec hardened with production-database techniques:
11. **BlockLocationIndex** — a persistent BlockId→(Offset,Length) B+-tree (LMDB page-table pattern), COW-updated at each Checkpoint and rebuilt by compaction. Closes the hole where ULID-only addressing would have forced an O(file) scan on open; open is now O(log n) always.
12. **Generic B+-tree node format** — one node layout with declared IndexKind/KeySize/ValueSize serves the primary index, location index, date index, and FTS; new indexes need no new block types.
13. **Operational safety rails**: CleanShutdown flag (skip recovery scan on clean open), MaxPayloadLength sanity bound + decompression bomb guard (never allocate from an unverified length), fsync-failure-is-fatal rule (fsyncgate), directory fsync on create/rename, compaction via side-file + atomic rename, enforced single-writer OS lock, live/dead byte accounting in Checkpoint to trigger compaction without scans, NFC password normalization before KDF, monotonic ULID mode for clock regression, little-endian/ULID-byte-order conventions made normative.
14. **Block type IDs renumbered gapless 0–21** — no legacy reservations since no v1/v2 files exist.

**Consequence**:
- `EmailDB_FileFormat_Spec.md` rewritten as v3 canonical; `docs/file_spec/*` must be updated to v3 (currently describe as-built v1)
- Implementation targets: superblock manager, ULID block IDs in RawBlockManager, ULID-addressed BTree rewrite, Checkpoint writer/reader, WAL-as-blocks, encryption bootstrap wiring with AAD, read-time Merkle verification
- Existing v1-era code paths to retire: OverrideLocation in-place writes, raw fixed-region BTree WAL, PrevChainHash serialization, int64 BlockIdGenerator range partitioning
- Open items carried forward: KeyStore sync ordering protocol (ExpertReport #16), sync coverage for Tier 2/search indexes (#15), .vec sidecar file format (#23)

## Known Bugs Found During Benchmarking
- **RawBlockManager footer int32/int64 mismatch**: `WriteBlockToStream` writes footer length as `int` (4 bytes) but `ReadBlockFromStreamInternal` reads it as `Int64` (8 bytes). Causes "Unable to read beyond the end of the stream" on every read.
- **CacheManager.InitializeNewFile corrupts RawBlockManager state**: Calling `WriteBlockAsync` with `OverrideLocation: 0` resets `currentPosition` to just past the header, causing subsequent writes to overwrite WAL/FolderTree/Metadata blocks.
- Both bugs tracked in EPIC-EMDB-1 (Core Storage Layer Stabilization).

## Development History
- **Initial commit**: Base project structure
- **Major build-out**: All core components, ZoneTree adapters, CapnProto/Protobuf layers, test suites
- **Refactoring**: Moved managers to FileManagement/ namespace, added helpers (BlockIDGenerator, BlockConverter, AsyncReaderWriterLock)
- **Benchmarking focus**: Added BenchmarkRunner, EmailBenchmark (900+ lines), unit test infrastructure
- **SQLite Benchmark (2026-02-22)**: Built comprehensive BenchmarkDotNet suite comparing EmailDB vs SQLite+FTS5. EmailDB confirmed as the right choice.
- **Architecture refinement (2026-02-22)**: Sidecar vector files, phased HNSW scaling, sharded .emdb files at ~50GB each.
- **Plan audit (2026-02-22)**: Added ADR-011 (text preparation), ADR-012 (HNSW persistence). Fixed stale tasks, added US-EMDB-24 (text pipeline). All story tasks aligned to acceptance criteria.
- **Protobuf consolidation (2026-02-22)**: ADR-001 resolved — chose protobuf-net over Google.Protobuf. Attribute-based serialization is the standard.
- **Cap'n Proto removal (2026-02-23)**: ADR-002 resolved — decided to remove Cap'n Proto layer entirely. Project was ~70% stubbed with no schema files. Protobuf is the sole serialization layer.
- **ZoneTree replacement (2026-02-23)**: ADR-013 — replaced ZoneTree with self-built append-only B+-tree. BLAKE3 hash chaining for integrity. EPIC-EMDB-3 archived, EPIC-EMDB-8 created with 10 stories / 65 points / 80 tasks. ADR-005 superseded.
- **KEK/DEK key-wrapping (2026-02-24)**: ADR-015 — replaced single master key encryption with two-tier KEK/DEK key-wrapping architecture. Password change re-encrypts only the key store block. Key rotation adds new DEK without touching old blocks. EPIC-EMDB-9 updated with new stories for key store, key wrapping provider, password change, and key rotation.
- **BLAKE3-128 checksums (2026-02-24)**: ADR-014 — upgraded block checksums from CRC32 to BLAKE3-128. Block fixed overhead now 91 bytes.
- **Documentation consolidation (2026-02-25)**: Deleted 19 scattered/outdated docs, created 7 focused spec documents in `/docs/`: BTree_Index, Compaction, Encryption, Folder_Listing, Search, Storage_Estimates, Sync.
- **IndexRoot simplification (2026-02-25)**: Replaced backward hash chain with monotonic sequence counter. Compaction discards chain anyway; Checkpoint is authoritative; sequence handles fallback recovery. IndexRoot payload shrinks from 90 to 58 bytes.
- **Current**: Ongoing development on InitialDev branch. Core block format and B+-tree implemented. Next milestones: fix core bugs (EPIC-EMDB-1), implement three-tier folder listing, search indexes, sync.