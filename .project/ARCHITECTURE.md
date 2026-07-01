# EmailDB — Architecture

**Format:** v3 (`EmailDB_FileFormat_Spec.md` is normative). Greenfield — no v1/v2 compatibility.

## Implementation Status

The v3 format is the build target. The existing code implements the retired v1 format (36 B headers, int64 BlockIds, offset-addressed BTree) and is being replaced subsystem-by-subsystem; see ADR-016 for the migration inventory. Nothing below should be read as "already built" unless the epic tracking says so.

## Layered Design

```
┌──────────────────────────────────────────────────────┐
│   EmailManager                                        │  High-level email API
├──────────────────────┬───────────────────────────────┤
│   Indexes (BTree)     │   Search (FTS/Vector/Bloom)   │  Primary + Location + Date | 5-phase search
├──────────────────────┼───────────────────────────────┤
│   CheckpointManager   │   FolderManager               │  Commit protocol | pages + deltas
├──────────────────────┴───────────────────────────────┤
│   BlockStore (append, cache, encrypt, verify)         │  Superblock + block I/O
├──────────────────────────────────────────────────────┤
│   .emdb file                     .emdb.vec sidecar    │
└──────────────────────────────────────────────────────┘
```

Higher layers never bypass lower layers. Everything above BlockStore deals in ULIDs only; physical offsets are BlockStore's private concern (runtime map + BlockLocationIndex).

## File Layout

```
mailbox/
├── emails_001.emdb          Shard 1 (~50 GB cap): superblocks + append-only block stream
├── emails_001.emdb.vec      Sidecar: embeddings + HNSW (derived, rebuildable)
├── emails_002.emdb          Shard 2
└── ...
```

Per file: two 4 KB superblock slots (A/B, alternating writes, highest valid sequence wins) then the append-only block stream. The superblock carries FileId (ULID), ShardIndex, feature flags (Compat/ReadOnlyCompat/Incompat), CleanShutdown flag, encryption bootstrap (KdfParams, Salt, verification token, KeyStore pointer), and a fast-open Checkpoint pointer. It is the only in-place structure and is torn-write safe by construction.

## Block Format (96 bytes fixed overhead)

- **Header** (48 B): magic, version, type, flags (bit 0 = encrypted), payload encoding, compression byte, **2-byte KeyEpoch**, **16-byte ULID BlockId**, payload length, 8 reserved
- **Header checksum** (16 B, BLAKE3-128) + **payload** + **payload checksum** (16 B, over ciphertext) + **footer** (16 B: ~magic + total length)
- No Timestamp field — the ULID's 48-bit ms timestamp is the timestamp
- Encrypted payloads add 28 B (12 B random nonce + 16 B tag) and are AAD-bound to `FileId‖BlockId‖BlockType‖KeyEpoch`
- Write path: Serialize → Compress → Encrypt → Checksum → Append; lengths validated against `MaxPayloadLength` before any allocation on read

## Block Types (v3, canonical)

| ID | Name | Purpose |
|----|------|---------|
| 0 | Metadata | Global file info (non-bootstrap) |
| 1 | WAL | Write-ahead log blocks (carry CheckpointBlockId replay fence) |
| 2 | FolderTree | Folder hierarchy |
| 3 | Cleanup | Dead-block accounting |
| 4 | BTreeLeaf | Generic B+-tree leaf (all indexes) |
| 5 | BTreeInternal | Generic B+-tree internal node |
| 6 | IndexRoot | Root descriptor (carries IndexKind) |
| 7 | EmailContent | Tier 3: raw MIME, attachments |
| 8 | KeyStore | KEK-encrypted DEK table |
| 9 | Checkpoint | Commit point + fast-open root table |
| 10 | EmailMetadata | Tier 2: full headers, MIME structure, Preview |
| 11 | FolderPageDirectory | Per-folder page index + FolderVersion |
| 12 | FolderPage | Tier 1: packed listing records (~80/page) |
| 13 | FolderDeltaLog | Chained folder change log |
| 14–17 | FTS* | Trigram index (always encrypted) |
| 18 | BloomFilter | Per-folder existence filter |
| 19–21 | Embedding/VectorNode/VectorRoot | `.emdb.vec` sidecar |

## Indexes — one node format, four trees

Generic node (declared IndexKind/KeySize/ValueSize) serves every tree; Merkle child-hashes are verified on every traversed path (write-only hashing is non-conforming).

| IndexKind | Maps | Notes |
|-----------|------|-------|
| 0 PrimaryEmail | EmailHashedID (SHA3-256) → BlockId | 83/leaf, 50-way; height 4 ≈ 10.4M emails |
| 1 **BlockLocation** | BlockId → (Offset, Length) | The indirection table: only offset-addressed structure, derived data, rebuilt by compaction; makes open O(log n) with ULID-only pointers |
| 2 Date | DateTicks‖BlockId → ∅ | Time-range queries |
| 3 FTS | trigram structures | See Search |

Copy-on-write (CouchDB model): mutations rewrite root-to-leaf paths; WAL-buffered flushes (~83 entries/batch); Checkpoint is the commit point, `IndexRoot.Sequence` only a no-checkpoint fallback. See [BTree Index](../docs/BTree_Index.md).

## Three-Tier Email Model + Folder Pages

| Tier | Type | Size | Read when |
|------|------|------|-----------|
| 1 | FolderPage (12) | ~400 B/email | Folder browsing — 2-3 block reads per page view |
| 2 | EmailMetadata (10) | ~4 KB | Opening an email; Preview field makes Tier 1 regenerable from Tier 2 alone |
| 3 | EmailContent (7) | variable | Body/attachments |

Per folder: FolderPageDirectory (date-ranged page entries, FolderVersion counter, delta head) → FolderPages (date-desc) + chained FolderDeltaLog blocks (append-only; compiled into pages at ~500 pending ops). No in-place rewriting anywhere. See [Folder Listing](../docs/Folder_Listing.md).

## Commit, Recovery, Durability

- **Checkpoint (type 9) is the commit point**: root table (folder tree, primary index, location index, metadata, KeyStore, previous checkpoint, generic secondary-index table) + live/dead byte accounting
- WAL blocks after Checkpoint N carry N's BlockId; recovery replays exactly those, then writes a fresh Checkpoint
- Open: superblock → (CleanShutdown=1? done) → bounded forward scan for newer Checkpoints/WAL. Never O(file) on a normal path
- fsync failure is fatal (poison handle, force recovery on reopen); directory fsync on create/rename/delete
- Corruption handling is a spec-level contract (spec Section 13) — required behavior per failure class

## Search

Five phases: address trigram FTS (14–17, always encrypted) → Tier 1 listing scan (~15 ms/50K folder) → date BTree → vector embeddings (`.emdb.vec`, HNSW, ADR-010/011) → per-folder bloom filters (18). All phases return EmailHashedIDs; the primary index resolves them. All search structures are rebuildable. See [Search](../docs/Search.md).

## Encryption

```
Password → NFC → Argon2id(superblock KdfParams/Salt) → KEK → KeyStore (DEK table) → per-block AES-256-GCM
```

- Random 12 B nonces; AAD binding; checksums on ciphertext; 2-byte epochs (65,535)
- Password change O(1) (KeyStore + superblock only, crash-safe via dual slots); rotation O(1) (new epoch)
- Default policy: content, folders, WAL, FTS, bloom encrypted; BTree nodes/Metadata/Checkpoint plaintext (recovery before keys; keyless integrity checks)
- Superblock KDF fields implicitly authenticated via the KeyVerificationToken

See [Encryption](../docs/Encryption.md).

## Compaction

Two mechanisms (an append-only file cannot reclaim interior space in place):
- **Inline reorganization**: delta compile, WAL clearance, epoch consolidation
- **Full-file compaction**: side-file rewrite + atomic rename + directory fsync — crash yields old-complete or new-complete, never hybrid. BlockLocationIndex rebuilt; no logical content rewritten (ULID pointers). Triggered by Checkpoint byte accounting (`Dead > 1× Live`), never by scanning. Optional `reEncrypt` consolidates epochs and prunes DEKs.

See [Compaction](../docs/Compaction.md).

## Sync (Active-to-Backup)

| Data | Mechanism |
|------|-----------|
| EmailContent/EmailMetadata | ULID high-water mark (immutable, ID-ordered) |
| Folder pages | FolderVersion comparison, wholesale transfer |
| KeyStore | Must precede new-epoch content (ordering protocol still open) |
| Indexes / delta logs / location index | Not synced — derived per replica |

Single authoritative writer; secondary machines submit mutations via actions-channel RPC. `CheckpointSequence` gives consistent restore points. See [Sync](../docs/Sync.md).

## Serialization

Pluggable via `PayloadEncoding` byte: Custom binary (index nodes), Protobuf (protobuf-net — primary for structured payloads, ADR-001), Json (debug), RawBytes. Compression byte: None/LZ4/Zstd/Brotli/Deflate, applied before encryption.

## Key Patterns

- **Append-only + dual-slot superblock** — one write model; the only in-place structure is torn-write safe
- **ULIDs are the durable pointers; offsets are derived data** (BlockLocationIndex) — compaction moves anything freely
- **O(log n) open, O(1) password change, scan only as disaster recovery**
- **Verified Merkle + AAD + checksums-on-ciphertext** — corruption, tampering, and wrong-key are three distinguishable failures with contracted behavior
- **Single writer (OS-enforced), snapshot readers** via Checkpoint + immutability
- **Sharding at ~50 GB**; `.vec` sidecar rebuildable, staleness detected by echoed CheckpointSequence
