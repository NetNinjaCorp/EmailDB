# EmailDB File Format Specification (v3)

**Status:** Canonical build target. Greenfield — no backward compatibility with v1/v2 artifacts is required or provided. Test files produced by pre-v3 code are not readable and do not need to be.

## 1. Overview

EmailDB stores email data, indexes, folder structures, and encryption keys in a single append-only file (`.emdb`), optionally accompanied by a rebuildable vector-search sidecar (`.emdb.vec`). The format is block-based with per-block checksums and optional per-block encryption.

### 1.1 Design principles

1. **Append-only.** Blocks are never overwritten. Updates write a new block version; old versions become dead and are reclaimed by compaction. The only in-place structure is the dual-slot superblock, which is torn-write safe by construction (Section 3).
2. **ULID identity; offsets are derived data.** Every block is identified by a 128-bit ULID. Logical structures never persist file offsets as durable pointers. Physical offsets live in exactly three places: the runtime map, the **BlockLocationIndex** (a rebuildable on-disk indirection table, Section 7), and root-pointer hints in the Checkpoint/superblock. Compaction can therefore move any block without rewriting logical content.
3. **O(log n) open, never O(file).** Opening a file requires the superblock, one Checkpoint, and index roots — no full scan on any normal path. Full scans exist only as disaster recovery.
4. **Verified integrity, not decorative integrity.** Every hash defined here has a defined read-time verification point. Hashes that are only ever written are a spec bug.
5. **Cryptographic binding.** Encrypted payloads are bound to block and file identity via AES-GCM associated data; ciphertext cannot be transplanted between blocks or files.
6. **Self-describing files.** Everything needed to open a file (format version, feature flags, KDF configuration, size limits) is stored in the file. Reader behavior never depends on compile-time constants.
7. **Single writer, many readers.** Enforced, not assumed (Section 12).
8. **Defined failure behavior.** Every verification failure has specified required behavior (Section 13). fsync failure is fatal, never retried (Section 10.3).

### 1.2 Conventions

- All multi-byte integers are **little-endian** unless stated otherwise.
- ULIDs are stored as **16 raw bytes in standard binary layout (big-endian)** so that byte-wise comparison equals chronological order.
- Strings are UTF-8, length-prefixed within their serialization format.
- "fsync" means flush-to-durable-media (`FileStream.Flush(true)` / `FlushFileBuffers` / `fdatasync`), never a stream-buffer flush.
- BLAKE3-128 = first 16 bytes of BLAKE3-256 output.

## 2. File Layout

```
Offset 0      Superblock slot A   (4096 bytes)
Offset 4096   Superblock slot B   (4096 bytes)
Offset 8192   Block stream        (append-only, grows to EOF)
```

## 3. Superblock

The superblock holds everything a reader needs before it can interpret blocks. Two fixed 4096-byte slots; writes alternate between slots, each write increments `SuperblockSequence` and is fsynced. On open, validate both slots (magic + checksum), use the higher valid sequence. A torn superblock write never destroys the previous good superblock.

### 3.1 Slot layout (4096 bytes)

| Field | Size | Description |
|-------|------|-------------|
| SuperblockMagic | 8 | `0x53E3A11DBB00DBEE` |
| FormatVersion | 2 | 3 |
| SuperblockSequence | 8 | Monotonic; higher valid slot wins |
| FileId | 16 | ULID minted at file creation; identifies this shard forever |
| ShardIndex | 4 | Zero-based shard index within the mailbox |
| CreatedTimestamp | 8 | UTC ticks at file creation |
| CompatFlags | 4 | Unknown bits: reader proceeds normally |
| ReadOnlyCompatFlags | 4 | Unknown bits: reader may open read-only, MUST NOT write |
| IncompatFlags | 4 | Unknown bits: reader MUST refuse to open |
| CleanShutdown | 1 | 1 = last session closed cleanly; 0 = crash recovery may be needed |
| MaxPayloadLength | 8 | Sanity bound for `PayloadLength` (default 268,435,456 = 256 MB) |
| LastCheckpointBlockId | 16 | ULID of a recent Checkpoint (0 = none yet) |
| LastCheckpointOffset | 8 | File offset of that Checkpoint |
| EncryptionEnabled | 1 | 0 = plaintext file, 1 = encrypted |
| AlgorithmId | 1 | 1 = AES-256-GCM |
| KdfType | 1 | 1 = Argon2id |
| KdfParams | 16 | Opaque per-KDF packing (Section 3.2) |
| Salt | 16 | CSPRNG salt for KDF |
| KeyVerificationToken | 32 | Nonce(12) + AES-GCM(KEK, "EMDB")(4) + Tag(16) |
| ActiveKeyStoreBlockId | 16 | ULID of the current KeyStore block |
| ActiveKeyStoreOffset | 8 | File offset of that KeyStore block |
| Reserved | 3898 | Must be 0 |
| SuperblockChecksum | 16 | BLAKE3-128 over bytes 0..4079 of the slot |

**Tamper note:** the superblock is not encrypted, but its encryption parameters are implicitly authenticated — any tampering with Salt, KdfParams, KdfType, or AlgorithmId changes the derived KEK, and the `KeyVerificationToken` (authenticated by the KEK under GCM) then fails, which is a distinct "wrong key or tampered header" error, not silent weakening.

### 3.2 KdfParams packing (Argon2id)

```
[MemoryKB (4B)] [Iterations (2B)] [Parallelism (2B)] [Reserved (8B, zero)]
```

Defaults: 65536 / 3 / 4. Readers MUST use stored values. Password change may upgrade KdfParams (Section 9.6).

### 3.3 Update policy and CleanShutdown protocol

Superblock rewrites (alternate slot) happen on: file creation, first write after open (sets `CleanShutdown = 0`), password change / key rotation, graceful close (final Checkpoint written, then `CleanShutdown = 1`), and periodically during long sessions.

`LastCheckpoint` is a **hint, not a commit point** — the Checkpoint block is the commit point (Section 10). On open with `CleanShutdown = 1`, the hinted Checkpoint is current and WAL scanning may be skipped. With `CleanShutdown = 0`, the reader MUST scan forward from the hinted Checkpoint for newer Checkpoints and unreplayed WAL blocks (Section 10.2).

## 4. Block Format

Every block in the block stream. Total fixed overhead: **96 bytes**.

```
+------------------+------+------------------------------------------------+
| Field            | Size | Description                                    |
+------------------+------+------------------------------------------------+
| HeaderMagic      | 8    | 0xEE411DBBD114EE                               |
| FormatVersion    | 2    | 3                                              |
| BlockType        | 1    | Enum (Section 5)                               |
| Flags            | 1    | Bit 0 = Encrypted; bits 1-7 reserved (0)       |
| PayloadEncoding  | 1    | Enum (Section 4.3)                             |
| Compression      | 1    | Enum (Section 4.4)                             |
| KeyEpoch         | 2    | DEK epoch, 0-65535 (0 when not encrypted)      |
| BlockId          | 16   | ULID (Section 4.2)                             |
| PayloadLength    | 8    | On-disk payload bytes (incl. nonce+tag if enc) |
| Reserved         | 8    | Must be 0                                      |
+------------------+------+------------------------------------------------+  = 48 B header
| HeaderChecksum   | 16   | BLAKE3-128 over the 48 header bytes            |
+------------------+------+------------------------------------------------+
| Payload          | var  | See processing order                           |
| PayloadChecksum  | 16   | BLAKE3-128 over the on-disk payload bytes      |
+------------------+------+------------------------------------------------+
| FooterMagic      | 8    | ~HeaderMagic (bitwise NOT)                     |
| TotalBlockLength | 8    | Entire block size including this footer       |
+------------------+------+------------------------------------------------+
```

**Processing order**
- Write: Serialize → Compress → Encrypt → Checksum → Append
- Read: Read → Verify HeaderChecksum → **Validate PayloadLength ≤ MaxPayloadLength before allocating** → Verify PayloadChecksum → Decrypt → Decompress → Deserialize

Rules:
- No Timestamp field: the ULID's top 48 bits are a millisecond UTC timestamp; in an append-only file creation time is write time.
- `PayloadChecksum` covers bytes as stored (ciphertext when encrypted) so integrity is verifiable without keys. Empty payload → 16 zero bytes.
- `TotalBlockLength` in the footer enables backward scanning from EOF.
- **Length sanity:** a header whose `PayloadLength` exceeds `MaxPayloadLength`, or whose block would extend past EOF, is corrupt — treat per Section 13; never allocate based on an unverified length.
- **Duplicate BlockIds:** the same BlockId may legitimately appear multiple times (new versions of Metadata, KeyStore, directory blocks). Later file position supersedes earlier. Deserializers MUST bounds-check all counts/lengths against the actual payload size.

### 4.1 Flags

Bit 0 = Encrypted. Bits 1-7 reserved, must be 0. There is deliberately no Checkpoint flag (BlockType suffices), no Tombstone flag (deletion belongs to compaction), and no algorithm flag (algorithm is file-global).

### 4.2 Block ID (ULID)

48-bit ms UTC timestamp + 80 bits CSPRNG randomness. Sortable as raw bytes, globally unique without coordination, doubles as the block timestamp, and gives sync a natural high-water mark. **Clock regression:** generators MUST use monotonic ULID mode — if the wall clock moves backward, continue incrementing from the last issued ULID rather than emitting out-of-order IDs.

### 4.3 PayloadEncoding

| Value | Name | Description |
|-------|------|-------------|
| 0 | Custom | Custom binary (BTree nodes) |
| 1 | Protobuf | protobuf-net |
| 2 | Reserved | |
| 3 | Json | Debug/interchange |
| 4 | RawBytes | Unstructured bytes |

### 4.4 Compression

Applied after serialization, before encryption. The compression frame carries the uncompressed length (LZ4/Zstd frame formats); it is not duplicated in the header. **Decompression bomb guard:** decoders MUST cap output at `MaxPayloadLength` × 16 and fail per Section 13 beyond it.

| Value | Algorithm |
|-------|-----------|
| 0x00 | None |
| 0x01 | LZ4 |
| 0x02 | Zstd |
| 0x03 | Brotli |
| 0x04 | Deflate |
| 0x05-0xFF | Reserved |

## 5. Block Types

Fresh, gapless numbering (greenfield — no legacy IDs to avoid). An ID, once shipped in a release, is never reused for a different meaning.

```
0   Metadata             Global file metadata (non-bootstrap; superblock holds bootstrap)
1   WAL                  Write-ahead log block (Section 10.4)
2   FolderTree           Folder hierarchy definition
3   Cleanup              Dead-block accounting for compaction
4   BTreeLeaf            Generic B+-tree leaf node (Section 6)
5   BTreeInternal        Generic B+-tree internal node
6   IndexRoot            B+-tree root descriptor (carries IndexKind)
7   EmailContent         Tier 3: raw MIME body, attachments
8   KeyStore             Encrypted DEK table (Section 9.2)
9   Checkpoint           Commit point + fast-open root table (Section 10)
10  EmailMetadata        Tier 2: full RFC 5322 headers, MIME structure, Preview
11  FolderPageDirectory  Per-folder page index (FolderVersion counter)
12  FolderPage           Tier 1: packed listing records (~80 emails/page)
13  FolderDeltaLog       Folder change log (chained append-only blocks)
14  FTSSegmentMeta       Trigram/FTS segment metadata
15  FTSTermDictionary    Trigram/FTS term dictionary
16  FTSPostingList       Trigram/FTS posting list
17  FTSSearchRoot        Trigram/FTS root pointer
18  BloomFilter          Per-folder existence filter
19  EmbeddingContent     Vector embedding data (.emdb.vec sidecar)
20  VectorIndexNode      HNSW/IVF node (.emdb.vec sidecar)
21  VectorIndexRoot      Vector index root (.emdb.vec sidecar)
22-239   Reserved
240-254  Experimental / vendor (never in release files)
```

FolderDeltaLog blocks chain via `PreviousDeltaBlockId`; the FolderPageDirectory points at the newest. Compiled deltas become dead and are reclaimed by compaction. Nothing outside the superblock is ever rewritten in place.

## 6. Generic B+-Tree Node Format

One node format serves every tree in the file — primary email index, BlockLocationIndex, date index, FTS trees. Key/value widths are declared per node, so new indexes never need new node types.

**Node header (12 bytes, at payload start):**

| Field | Size | Description |
|-------|------|-------------|
| NodeKind | 1 | 0 = leaf, 1 = internal |
| NodeVersion | 1 | Node format version (1) |
| IndexKind | 2 | Which index this node belongs to (registry in 6.1) |
| KeySize | 1 | Bytes per key |
| ValueSize | 2 | Bytes per leaf value / internal child record |
| EntryCount | 2 | Number of entries |
| Reserved | 3 | Must be 0 |

Leaf body: `EntryCount × (KeySize + ValueSize)`, sorted by key.
Internal body: `EntryCount` routing keys of `KeySize`, then `EntryCount + 1` child records of `ValueSize`.

**Merkle integrity:** internal child records embed `ChildHash` = BLAKE3-256 of the child node's full serialized payload. `IndexRoot.RootHash` covers the root node. Readers MUST verify each traversed node against its parent's ChildHash (path verification); a Full verification walks the whole tree. There is no PrevChainHash — the per-node backward chain is removed (no threat model; compaction discards it; Merkle already gives tamper evidence).

**IndexRoot payload (68 B):** `IndexKind (2) + RootBlockId (16) + EntryCount (8) + TreeHeight (2) + RootHash (32) + Sequence (8)`. `Sequence` is monotonic per index; a fallback recovery aid only — the Checkpoint is authoritative. No sibling pointers in leaves (CouchDB-style COW; range scans backtrack through the parent).

### 6.1 IndexKind registry

| Kind | Index | Key | Leaf value | Internal child record |
|------|-------|-----|------------|----------------------|
| 0 | PrimaryEmail | EmailHashedID (32, SHA3-256) | BlockId (16) | ChildBlockId (16) + ChildHash (32) = 48 |
| 1 | BlockLocation | BlockId (16) | Offset (8) + Length (8) = 16 | **ChildOffset (8)** + ChildHash (32) = 40 |
| 2 | Date | DateTicks (8) ‖ BlockId (16) = 24 composite | (empty, 0) | ChildBlockId (16) + ChildHash (32) = 48 |
| 3 | FTS | (defined in [Search](docs/Search.md)) | | |
| 4-99 | Reserved | | | |
| 100+ | Experimental | | | |

Capacities at a 4096-byte target node (3988 usable after 96 B block overhead + 12 B node header): PrimaryEmail leaf 83 entries, internal 49 keys / 50 children; BlockLocation leaf 124 entries, internal 70 keys / 71 children; Date leaf 166 entries, internal 54 keys / 55 children.

## 7. BlockLocationIndex (the indirection table)

**Problem it solves:** with ULID-only logical pointers, something must map BlockId → physical offset, or every open degenerates to a full file scan. This is the same role as LMDB's page table or an LSM manifest.

- A B+-tree (IndexKind 1) mapping `BlockId → (Offset, Length)` for **every live block** in the file.
- **It is the one structure allowed to use raw offsets internally** (its internal nodes point to children by offset, not ULID) — it *is* the offset map, so it cannot depend on itself. This is safe because it is **derived data**: compaction rebuilds it from scratch for the new file, and it can always be regenerated by a full scan.
- Updated copy-on-write at each Checkpoint: entries for blocks appended since the last Checkpoint are batch-inserted, changed paths written as new nodes, then its IndexRoot, then the Checkpoint referencing it.
- Lookup path for any block: `BlockLocationIndex.Get(BlockId)` → O(log n) with upper nodes cached. Blocks appended after the latest Checkpoint are not yet in the index; they are found by the bounded forward scan from the Checkpoint offset during recovery (Section 10.2) and live in the runtime map.
- Location resolution precedence: runtime map → BlockLocationIndex → (disaster only) full scan.

Cost at 10M blocks: ~124 entries/leaf → ~81K leaves ≈ 330 MB total index, but per-checkpoint delta writes are only the touched paths (log n per batch). Open cost: superblock → Checkpoint → two index roots. No scan.

## 8. Three-Tier Email Model (block-level contract)

| Tier | BlockType | Contents | Read on |
|------|-----------|----------|---------|
| 1 | FolderPage (12) | Packed listing records (~400 B/email) | Folder browsing |
| 2 | EmailMetadata (10) | Full headers, MIME structure, threading refs, **Preview (~200 chars)** | Opening an email |
| 3 | EmailContent (7) | Raw MIME body, attachments | Body/attachment access |

Preview in Tier 2 guarantees Tier 1 pages regenerate from Tier 2 alone. Details in [Folder Listing](docs/Folder_Listing.md).

## 9. Encryption

### 9.1 Key hierarchy

```
Password ──Argon2id(KdfParams, Salt)──▶ KEK (32 B)
KEK ──AES-256-GCM──▶ KeyStore block payload (DEK table)
DEK[epoch] ──AES-256-GCM──▶ data block payloads
```

**Password canonicalization:** passwords are Unicode-normalized to **NFC**, then UTF-8 encoded, before KDF input. (Without this, the same password typed on macOS vs Windows can produce different bytes and fail to open the file.) No length cap below 1024 bytes. Implementations MUST zeroize password bytes, KEK, and DEK buffers when their scope ends.

### 9.2 KeyStore block (type 8)

Protobuf payload, entirely KEK-encrypted: `KeyStoreVersion`, `ActiveEpoch (uint16)`, `Entries[] { Epoch (uint16), DEK (32 B), CreatedTimestamp, Retired (bool) }`. Writers MUST NOT stamp a retired epoch on new blocks; rotation MUST fail rather than exceed epoch 65535 (compaction with re-encryption consolidates epochs and prunes unreferenced DEKs).

### 9.3 Per-block encryption

- AES-256-GCM (file-global algorithm). On-disk encrypted payload: `Nonce (12) ‖ Ciphertext ‖ Tag (16)` → +28 B.
- **Nonce: 12 fully random CSPRNG bytes per encryption operation.** (ULID-derived nonces rejected: compaction re-encrypting the same BlockId under the same DEK would collapse uniqueness to 4 bytes; GCM nonce reuse is catastrophic.)
- **AAD (35 bytes), mandatory on encrypt and decrypt:**

```
AAD = FileId (16) ‖ BlockId (16) ‖ BlockType (1) ‖ KeyEpoch (2, LE)
```

Binds ciphertext to its identity: a valid ciphertext moved to another block, type, epoch, or file fails authentication even though all checksums pass. Compaction that re-encrypts recomputes AAD with the (unchanged) BlockId and the new epoch.

### 9.4 Checksum interaction

`PayloadChecksum` covers ciphertext. Verify order: checksum → GCM tag. Checksum failure = corruption; tag failure after good checksum = wrong key or tampering — distinct error classes (Section 13).

### 9.5 Encryption policy

| BlockType | Default | Full |
|-----------|---------|------|
| EmailContent, EmailMetadata, FolderTree, FolderPage, FolderPageDirectory, FolderDeltaLog, WAL | Encrypted | Encrypted |
| FTS (14-17), BloomFilter (18) | **Always encrypted** (trigrams/filters reverse to content) | Encrypted |
| BTreeLeaf/Internal/IndexRoot (4-6) | Plaintext (keys are hashes; integrity checkable without keys) | Encrypted |
| Metadata, Cleanup, Checkpoint | Plaintext (needed before keys are available) | Plaintext |
| KeyStore | Always KEK-encrypted | Always KEK-encrypted |

### 9.6 Password change — O(1)

1. Derive old KEK (stored Salt/KdfParams); decrypt KeyStore
2. New Salt (optionally upgraded KdfParams); derive new KEK
3. Append re-encrypted KeyStore block
4. Superblock write (alternate slot): new Salt, KdfParams, KeyVerificationToken, KeyStore pointer

Crash between 3 and 4: old superblock still valid, old KeyStore still present — file opens with the old password. Data blocks untouched.

### 9.7 Key rotation — O(1)

Append a KeyStore block with a fresh DEK at `ActiveEpoch + 1`; superblock update. Old blocks keep their epoch; DEKs prune once compaction re-encryption leaves them unreferenced.

## 10. Checkpoint, Commit, WAL, Durability

### 10.1 Checkpoint block (type 9) — the commit point

| Field | Type | Description |
|-------|------|-------------|
| FormatVersion | ushort | |
| CheckpointSequence | ulong | Monotonic across checkpoints |
| FileId | Ulid | Must match superblock (cross-check) |
| FolderTreeRootBlockId / Offset | Ulid + long | |
| PrimaryIndexRootBlockId / Offset | Ulid + long | IndexKind 0 root |
| **LocationIndexRootBlockId / Offset** | Ulid + long | IndexKind 1 root (Section 7) |
| MetadataBlockId / Offset | Ulid + long | |
| KeyStoreBlockId / Offset | Ulid + long | |
| PreviousCheckpointBlockId / Offset | Ulid + long | Checkpoint chain |
| SecondaryIndexCount | ushort | |
| SecondaryIndexes[] | { IndexKind (ushort), BlockId (Ulid), Offset (long) } | Date, FTS, bloom, future |
| LiveBlockCount | long | |
| **LiveByteCount / DeadByteCount** | long + long | Drives compaction triggers without scanning (Section 11.2) |

Offsets are verified hints: on use, confirm the block at the offset carries the expected BlockId; mismatch → re-resolve via BlockLocationIndex (never an error by itself).

### 10.2 Open protocol

1. Read both superblock slots; higher valid sequence wins. Unknown `IncompatFlags` → refuse; unknown `ReadOnlyCompatFlags` → read-only.
2. If encrypted: NFC-normalize password → KEK → check `KeyVerificationToken` (fast wrong-password detection) → decrypt KeyStore.
3. Follow `LastCheckpoint` hint. If `CleanShutdown = 1`: done — roots loaded, no scanning.
4. If `CleanShutdown = 0`: scan forward from the hinted Checkpoint for newer valid Checkpoints, take the newest; then scan forward from it for WAL blocks carrying its `CheckpointBlockId` and replay them (10.4); write a fresh Checkpoint. This scan is bounded by data written after the last checkpoint, not file size.
5. Load BlockLocationIndex root — all block resolution is now O(log n).
6. Fallbacks: no valid superblock → full scan from 8192 (Section 13); no valid Checkpoint → full scan rebuild.

### 10.3 Durability rules

- Batch commit: write data/node blocks (incl. BlockLocationIndex deltas) → fsync → write IndexRoots + Checkpoint → fsync.
- Group commit permitted (many logical ops per Checkpoint).
- **fsync failure is fatal.** If fsync reports an error, the write's durability is unknowable and the OS may have dropped the dirty pages (the "fsyncgate" lesson): the implementation MUST poison the file handle, refuse further writes, and force close + crash recovery on reopen. Never catch-and-retry fsync.
- File creation, compaction swap, and deletion MUST fsync the **containing directory** to persist the directory entry.

### 10.4 WAL blocks (type 1)

Standard append-only blocks — no raw regions, no in-place rewrites. Payload: `{ WalSequence (ulong), CheckpointBlockId (Ulid), Entries[] { Op, Key (32), BlockId (Ulid), aux } }`.

Protocol: WAL blocks written after Checkpoint N carry N's BlockId. Recovery replays exactly the WAL blocks whose `CheckpointBlockId` matches the last valid Checkpoint, then writes a fresh Checkpoint. WAL blocks referencing older checkpoints are committed history (dead).

## 11. Operations

### 11.1 Core

- **Initialize:** superblock A (seq 1) + B (seq 2), Metadata, KeyStore (if encrypted), FolderTree, empty index roots, first Checkpoint, directory fsync.
- **Write:** append block; update runtime map; entry joins BlockLocationIndex at next Checkpoint.
- **Read:** resolve via runtime map → BlockLocationIndex; verify checksums (+ tag/AAD if encrypted); decompress; deserialize with bounds checks.
- **Open:** Section 10.2.

### 11.2 Compaction

Triggered from Checkpoint accounting (`DeadByteCount > threshold × LiveByteCount`) — no scan needed to decide.

Swap protocol (atomic, crash-safe):
1. Write `<name>.emdb.compact`: fresh superblocks (same FileId, `SuperblockSequence` continues, incremented), live blocks copied, BlockLocationIndex rebuilt from scratch, fresh Checkpoint
2. fsync the new file
3. Atomic rename over `<name>.emdb`
4. fsync the directory
5. A crash at any point leaves either the old complete file or the new complete file — never a hybrid. A leftover `.compact` file found on open is deleted (it was never renamed, so it was never current).

Because logical structures hold only ULIDs, no logical block content is rewritten. Optional re-encryption to the active epoch may rewrite payloads (BlockIds unchanged; AAD recomputed). EmailContent blocks are otherwise copied verbatim. Concurrent readers on POSIX keep the old inode until they close; on Windows the swap uses `ReplaceFile` semantics.

## 12. Concurrency and Locking

- **Single writer, enforced:** the writer holds an OS-level exclusive lock (`FileShare.Read` on Windows/.NET; `flock`/OFD lock on POSIX) for the file's lifetime. A second writer MUST fail fast with a clear error, not corrupt.
- **Readers:** open read-shared. A reader that loads a Checkpoint has a consistent snapshot — append-only guarantees every block it can reach is immutable. Long-lived readers survive compaction via the old inode (POSIX) / pending-delete semantics (Windows).
- **In-process:** one writer thread owns the append position; readers need no lock on immutable blocks; the runtime map and caches use concurrent structures. The WAL buffer/flush boundary is the only writer-side critical section.

## 13. Corruption Handling Contract

Required behavior — implementations MUST NOT improvise:

| Failure | Meaning | Required behavior |
|---------|---------|-------------------|
| One superblock slot invalid | Torn superblock write | Use other slot; rewrite bad slot on next update |
| Both slots invalid | Severe damage | Refuse normal open. Recovery mode: full scan from 8192 rebuilds blocks + indexes, but encryption bootstrap (Salt/KdfParams/token) is unrecoverable without a copy — surface this explicitly |
| HeaderChecksum mismatch | Corrupt/torn header | Block dead. Resynchronize: scan forward for next valid HeaderMagic + checksum; log damaged range |
| PayloadLength insane (> MaxPayloadLength or past EOF) | Corrupt header that passed no checks yet | Same as header corruption; never allocate first |
| PayloadChecksum mismatch | Corrupt payload | Block dead; resynchronize. If referenced live → data-loss error naming the BlockId |
| GCM tag / AAD failure (checksum OK) | Wrong key or tampering | Distinct error class, not "corruption". Never brute other epochs beyond the header's KeyEpoch |
| Merkle ChildHash mismatch | Index corruption/tampering | Fail lookup; fall back to previous Checkpoint's root; if it verifies, abandon newer root and re-replay WAL. Repeated → rebuild index from EmailMetadata blocks |
| Checkpoint invalid | Torn commit | Walk `PreviousCheckpointBlockId` chain. Post-checkpoint blocks are uncommitted **except** matching WAL blocks (replayed) |
| No valid Checkpoint | Major damage | Full sequential scan; rebuild all indexes incl. BlockLocationIndex; write fresh Checkpoint |
| Offset hint resolves to wrong BlockId | Stale hint / misdirected write | Re-resolve via BlockLocationIndex; refresh hint at next Checkpoint. Not an error |
| Footer magic missing at EOF | Torn final append | Logical truncation at last valid block; the append was uncommitted by definition |
| Decompressed size exceeds bomb guard | Corrupt/malicious payload | Treat as payload corruption |

## 14. Immutability Model

| Component | Mutable? | Notes |
|-----------|----------|-------|
| Superblock slots | Rewritten in place | Dual-slot + sequence + checksum = atomic; the only in-place structure |
| EmailContent | Immutable | Copied verbatim by compaction (unless re-encrypting) |
| EmailMetadata / FolderPage / FolderPageDirectory / FolderDeltaLog | Append-only | New versions appended; old become dead |
| BTree nodes / IndexRoots (all IndexKinds) | Append-only (COW) | Mutation rewrites root-to-leaf path |
| Checkpoint / WAL / Metadata / KeyStore | Append-only | New version per change |
| BlockLocationIndex | Append-only (COW); rebuilt by compaction | Derived data; the only offset-addressed structure |
| File offsets elsewhere | Runtime + verified hints | ULIDs are the durable pointers |

## 15. Sync and Sharding Hooks

- `FileId` identifies a shard across machines; `ShardIndex` orders shards; `CheckpointSequence` gives consistent restore points
- ULID BlockIds → high-water-mark replication for immutable content; `FolderVersion` → folder-page replication
- KeyStore blocks must arrive before content blocks of a new epoch (ordering protocol still open — ExpertReport #16)
- `.emdb.vec` sidecar staleness detection: the sidecar header echoes the `CheckpointSequence` it was built from; mismatch on open → rebuild

## 16. Decisions Log (v3)

| Decision | Choice | Alternative rejected |
|----------|--------|----------------------|
| Block identity | ULID everywhere; offsets are derived data | int64 counters / offset-addressed indexes — made compaction impossible |
| BlockId → offset resolution | Persistent BlockLocationIndex (LMDB-page-table pattern), rebuilt by compaction | Full scan on open (O(file), the v1 failure); offset hints baked into logical nodes (defeats compaction-safety) |
| Node format | One generic node with declared KeySize/ValueSize + IndexKind | Per-index node types — new block types per future index |
| Header Timestamp | Removed; ULID supplies it | 8 B explicit field |
| Hot updates | Pure append-only incl. FolderDeltaLog | v2 fixed rewrite region — second write model, torn-write cases |
| File header | Dual-slot superblock + CleanShutdown flag | Single in-place header (crash-unsafe); header-as-block (no O(1) open) |
| Nonce | 12 random bytes | ULID-derived — re-encryption collision risk |
| Key epoch | 2 B header field (65,535) | Flags bits (127) — too tight with re-encrypting compaction |
| AAD binding | FileId‖BlockId‖BlockType‖KeyEpoch | None — allowed ciphertext transplant |
| PrevChainHash | Removed | No threat model; compaction discards it |
| fsync errors | Fatal, poison handle | Retry — durability unknowable after first failure |
| Compaction | Side-file + atomic rename + dir fsync | In-place compaction — torn state on crash |
| Password bytes | NFC-normalize before KDF | Raw platform encoding — cross-platform lockouts |
| Compaction trigger | Live/dead byte accounting in Checkpoint | Scanning to decide — O(file) maintenance |
| Block type IDs | Fresh gapless 0-21 | Preserving v1 numbering — no files exist to honor |
