# EmailDB — Architecture

## Layered Design

```
┌──────────────────────────────────────────────────────┐
│   EmailManager                                        │  High-level email API
├──────────────────────┬───────────────────────────────┤
│   BTreeIndex          │   VectorSearchManager         │  KV indexing + HNSW search
├──────────────────────┤───────────────────────────────┤
│   CacheManager        │   .emdb.vec Sidecar I/O      │  Block caching + vector persistence
├──────────────────────┤───────────────────────────────┤
│   RawBlockManager     │   IVectorIndex (HNSW)         │  Binary I/O + similarity search
├──────────────────────┴───────────────────────────────┤
│   .emdb File                    .emdb.vec Sidecar     │
└──────────────────────────────────────────────────────┘
```

## File Layout (per shard)

```
mailbox/
├── emails_001.emdb          Email data, folders, metadata (up to ~50GB)
├── emails_001.emdb.vec      Sidecar: embeddings + HNSW index
├── emails_002.emdb          Next shard
├── emails_002.emdb.vec      Sidecar for shard 2
└── ...
```

- `.emdb` — email content, folder structures, metadata, WAL, BTree index (append-only block format)
- `.emdb.vec` — embedding vectors + HNSW graph + email ID mapping (independent lifecycle)
- Sidecar can be deleted and rebuilt from .emdb data
- Web frontend only needs .vec files to serve search

## Block Format (91 bytes fixed overhead + variable payload)
- **Header** (37 bytes): Magic, version, type, flags/key-epoch, encoding, timestamp, block ID (ULID), payload length
- **Header Checksum** (16 bytes): BLAKE3-128
- **Payload** (variable): Serialized content
- **Payload Checksum** (16 bytes): BLAKE3-128
- **Footer** (22 bytes): Footer magic, total block length

With encryption enabled, each encrypted block adds 28 bytes overhead (12B nonce + 16B auth tag).

## Block Types

| Type ID | Name | Purpose |
|---------|------|---------|
| 0 | Metadata | Global file info, root references |
| 1 | FolderTree | Folder hierarchy with block references |
| 2 | FolderContent | Per-folder email ID list (source of truth for membership) |
| 3 | WAL | Write-ahead log entries |
| 4 | Cleanup | Superseded/deleted block markers |
| 5 | Checkpoint | Points to authoritative IndexRoot |
| 6 | BTreeLeaf | B+-tree leaf nodes (sorted key-value entries) |
| 7 | BTreeInternal | B+-tree internal nodes (routing keys + child pointers + child hashes) |
| 8 | IndexRoot | Root pointer + sequence counter + entry count + tree height |
| 9 | EmailContent | Tier 3: Raw MIME body, inline images, attachments |
| 10 | KeyStore | Encrypted DEK table (wrapped with KEK) |
| 12 | EmailMetadata | Tier 2: Full headers, MIME structure, threading refs |
| 13 | FolderPageDirectory | Per-folder page index with FolderVersion counter |
| 14 | FolderDeltaLog | Append-only change log for folder pages |
| 19 | BloomFilter | Per-folder probabilistic existence filter |
| 20 | EmbeddingContent | Vector embedding data per email |
| 21 | VectorIndexNode | HNSW/IVF index tree nodes |
| 22 | VectorIndexRoot | Vector index root pointer |

## Three-Tier Email Model

Email data is split by access temperature to minimize I/O for common operations:

| Tier | What | Size | When Read |
|------|------|------|-----------|
| **Tier 1** | Listing records packed into folder pages | ~400 bytes/email | Folder browsing (1 block per page of ~80 emails) |
| **Tier 2** | Full RFC5322 headers + MIME structure | ~4 KB/email | Opening a specific email |
| **Tier 3** | Raw MIME body + attachments | Variable | Viewing email body or downloading attachments |

Listing one page of a 50K-email folder: 2-3 block reads (~35 KB, 1 decrypt per block). See [Folder Listing](../docs/Folder_Listing.md).

## Folder Page System

Each folder uses paginated listing pages with a delta log:

```
FolderPageDirectory (1 block per folder, BlockType = 13)
  |
  +-- FolderPage 0 (newest ~80 emails, sorted by date desc)
  +-- FolderPage 1
  +-- FolderPage N
  |
  +-- FolderDeltaLog (append-only, BlockType = 14)
```

- **FolderPageDirectory** tracks page index entries with date ranges (enables binary search by date)
- **FolderDeltaLog** buffers Add/Delete/FlagChange operations (~432 bytes per email add)
- Delta log compiles into pages when threshold (~500 entries) is exceeded
- `FolderVersion` counter on the directory drives sync replication

## BTree Primary Index

Custom append-only B+-tree mapping `EmailHashedID` (32B SHA3-256) → `BlockId` (16B ULID). 48 bytes per entry.

- **Copy-on-write** (CouchDB model): mutations rewrite only the root-to-leaf path
- **WAL buffered**: inserts accumulate in WAL, flush at 82 entries (one full leaf)
- **Merkle integrity**: internal nodes carry BLAKE3 child hashes; IndexRoot stores RootNodeHash
- **Recovery**: IndexRoot has a monotonic Sequence counter (highest = latest). Checkpoint block points to the authoritative IndexRoot.
- **No backward hash chain**: compaction discards chain anyway; Checkpoint is authoritative

| Scale | Height | Leaf Nodes | Index Size |
|-------|--------|------------|------------|
| 10K | 3 | 176 | ~511 KB |
| 100K | 4 | 1,755 | ~4.9 MB |
| 1M | 4 | 17,544 | ~49 MB |
| 10M | 5 | 175,439 | ~494 MB |

See [BTree Index](../docs/BTree_Index.md).

## Search Architecture

Five-phase strategy ordered by implementation priority:

1. **Address trigram index** — instant substring matching on From/To/Cc. Combined index, always encrypted (trigrams are reversible). See [Search](../docs/Search.md).
2. **Listing page scan** — sequential scan of Tier 1 subject/preview fields. Folder-scoped: ~15ms for 50K emails.
3. **Date BTree** — secondary BTree keyed on date ticks for time-range queries.
4. **Vector embeddings** — semantic search via HNSW in sidecar `.emdb.vec` file. Handles conceptual/fuzzy queries.
5. **Bloom filters** — per-folder quick elimination before full page scan.

## Encryption

AES-256-GCM per-block encryption with two-tier key hierarchy:

```
Password → Argon2id → KEK → wraps/unwraps DEKs in KeyStore block
                              DEKs encrypt block payloads (one DEK per epoch)
```

- Password change re-encrypts only the KeyStore (~5 KB), not data blocks
- Key rotation adds a new DEK epoch; old blocks keep their original DEK
- Block header `KeyEpoch` field (bits 1-7 of Flags byte) identifies which DEK to use
- Default policy: encrypt email content, folders, WAL; leave BTree nodes and metadata plaintext

See [Encryption](../docs/Encryption.md).

## Sync (Active-to-Backup)

One-way replication, single authoritative writer:

| Data | Mechanism |
|------|-----------|
| EmailContent blocks | ULID high-water mark (immutable, content-addressed) |
| Folder pages | FolderVersion comparison, wholesale page transfer |
| KeyStore | Sent before new-epoch content blocks |
| BTree/Offsets/DeltaLog | Not synced — each replica maintains its own |

Multi-machine writes use an actions channel RPC: secondary submits mutations to the primary writer.

See [Sync](../docs/Sync.md).

## Compaction

Tiered compaction for the append-only, copy-on-write model:

| Level | Trigger | Scope |
|-------|---------|-------|
| L1 | Dead BTree blocks > 2x live | BTree nodes + IndexRoot only |
| L2 | Delta log threshold | Per-folder page rebuild |
| L3 | File size > 2x live data | Full file rewrite |

With `reEncrypt = true`, rewritten blocks get the active DEK epoch. See [Compaction](../docs/Compaction.md).

## Serialization
Pluggable via `IBlockContentSerializer` / `IPayloadEncoding` interface:
- **Protobuf** (primary) — protobuf-net with `[ProtoContract]` attributes (ADR-001)
- **Custom binary** — BTree nodes use `BinaryPrimitives` for fixed-size fields (faster than protobuf for fixed layouts)
- JSON (debug/interchange)
- RawBytes (passthrough)

## Key Patterns
- **Append-only writes** — old versions preserved for journaling/versioning
- **BLAKE3-128 integrity** — checksums on both header and payload
- **Tiered compaction** — three levels from lightweight BTree-only to full file rewrite
- **Thread safety** — `ReaderWriterLockSlim` for concurrent access
- **Cache layer** — `CacheManager` for frequently accessed blocks
- **Sharding** — .emdb files capped at ~50GB, auto-shard to new file
- **Sidecar independence** — .vec files rebuildable from .emdb source data
- **BlockId = ULID** — durable pointer (not file offsets); offsets resolved at runtime via `Dictionary<Ulid, long>`
