# External Review Response — Format Gap Analysis

**Date:** 2026-02-25
**Status:** Decisions finalized
**Context:** Response to external architecture review of EmailDB file format

### Sync Architecture (v1)

- **Active to Backup:** One-way replication. The active instance is the single writer; backup receives replicated state.
- **EmailContent sync:** ULID high-water mark. Backup says "I have up to ULID X" and active sends everything after X. EmailContent blocks are content-addressed (same hash = idempotent), so duplicates are conflict-free.
- **Folder sync:** FolderVersion comparison. Backup reports its FolderVersion per folder; if stale, active sends the current folder chain blocks wholesale and backup replaces its pages.
- **Multi-machine writes:** Out of format scope. The recommended pattern is an application-layer **actions channel** (RPC) where secondary machines submit mutations to the primary, which applies them and replicates results through normal Active to Backup replication.

---

## What's Validated (No Action Needed)

The reviewer confirmed these are sound — keep as-is:

- **Append-only, block-based, versioned-by-append** — clean backbone for replication and compaction
- **Integrity layout** — header checksum + payload checksum + footer with total length; 81-byte fixed overhead is good engineering hygiene
- **Three-tier model + packed folder pages** — correct answer for real-world mailboxes, 2-3 reads instead of 200,000+
- **ULID for BlockId** — worth the trade if sync is a goal (it is)

---

## Gap 1: BlockId Size Conflict (8 vs 16 bytes)

### The Problem

The file format spec defines `Block ID = 8 bytes` (`EmailDB_FileFormat_Spec.md`, block header table). The ULID proposal defines `BlockId = 16 bytes` (`Sync_and_ULID.md`, Section 3). Code currently uses `long BlockId` (`Block.cs:21`).

This is a hard conflict — every on-disk structure is affected.

### Reviewer's Options

- **Option A (clean):** v2 format uses 16-byte BlockId everywhere. Commit, bump version, migrate.
- **Option B (bridge):** Keep 8-byte BlockId in v1, add separate 16-byte "Commit ULID" only for sync checkpoints. Move to full ULID later.

### Recommendation: Option A — 16-byte ULID BlockId in v2

**Rationale:**

1. Sync is a stated goal. Half-measures create two ID spaces and a permanent mapping layer between them.
2. The BTree impact is already quantified (`Sync_and_ULID.md`, Section 5): 82 → 70 entries/leaf, no tree height change at any practical scale.
3. Every `Dictionary<long, ...>` and `BlockId` reference changes once, cleanly.
4. Option B means every sync operation needs a lookup table between two ID namespaces — that's strictly worse than paying 8 bytes per reference.

**Concrete changes:**

- Block header grows from 37 to 45 bytes
- Total fixed overhead goes from 81 to 89 bytes
- `long BlockId` in code becomes `Ulid` (128-bit struct)
- All `Dictionary<long, ...>` become `Dictionary<Ulid, ...>`
- `BlockIdGenerator` range-based scheme is replaced by ULID generation with monotonic guarantee per-process

---

## Gap 2: Flags Bit Layout — "Reserved" vs. Reality

### The Problem

The spec says Flags is "Reserved for future use" (`EmailDB_FileFormat_Spec.md:28`). But the code already uses the entire byte:

```csharp
// Block.cs
public const byte FlagEncrypted = 0x01;           // Bit 0: encrypted
public byte KeyEpoch => (byte)((Flags >> 1) & 0x7F);  // Bits 1-7: key epoch (0-127)
```

The spec is behind the code. Worse: **the entire byte is consumed.** There are zero free bits for compression, tombstone, checkpoint, or any other flag the reviewer (correctly) says we need.

### Options

| Option | Change | Tradeoff |
|--------|--------|----------|
| A. Expand Flags to 2 bytes | Header grows 1 byte | Gives 8 more bits but KeyEpoch still cramped into flags |
| B. Expand Flags to 4 bytes | Header grows 3 bytes | Future-proof but wasteful |
| **C. Separate KeyEpoch field** | Add dedicated 2-byte field, reclaim Flags byte | Clean separation of concerns |

### Recommendation: Option C — Separate KeyEpoch from Flags

Move KeyEpoch into its own 2-byte field. Reclaim Flags for actual boolean flags.

**New header layout (with ULID change from Gap 1):**

```
| Field              | Size   | Notes                                    |
|--------------------|--------|------------------------------------------|
| Header Magic       | 8      | 0xEE411DBBD114EEUL                       |
| Version            | 2      | Block format version (bumped to 2)       |
| Block Type         | 1      | Enum                                     |
| Flags              | 1      | Bit field (see below)                    |
| Payload Encoding   | 1      | Enum                                     |
| Key Epoch          | 2      | DEK epoch for encryption (0-65535)       |
| Timestamp          | 8      | UTC Ticks                                |
| Block ID           | 16     | ULID (128-bit)                           |
| Payload Length      | 8      | Length of payload data                    |
| Header Checksum    | 16     | BLAKE3-128 of header bytes               |
```

**Header size:** 47 bytes (was 37). **Total fixed overhead:** 91 bytes (was 81).

**Flags bit layout:**

```
Bit 0: Encrypted         (0 = plaintext, 1 = encrypted)
Bit 1: Compressed         (0 = none, 1 = compressed; algorithm in Payload Encoding)
Bit 2: Tombstone          (0 = live, 1 = logically deleted)
Bit 3: Checkpoint         (0 = normal, 1 = checkpoint block)
Bits 4-7: Reserved
```

**KeyEpoch at 2 bytes** gives 0-65535 epochs instead of the current 0-127. For a long-lived archive with periodic key rotation, 127 is tight. 65535 is more than sufficient.

**The 10-byte overhead increase (81 → 91) is negligible.** At 10M emails averaging 50 KB each, 10 extra bytes per block is 0.02% overhead.

---

## Gap 3: Checkpoint Block for Crash Consistency

### The Problem

On startup, the system scans blocks sequentially to rebuild `blockLocations`. The latest Metadata block is found by scanning. There is no explicit "this root is committed" marker.

This means:
- **Startup is O(file size)** — every block header must be read to find the latest metadata
- **Crash recovery is ambiguous** — if the file ends mid-block, which metadata block is authoritative?
- **Sync resume has no anchor** — no definitive "known good state" marker

### Recommendation: Add Checkpoint Block Type

A small block written as the final step of any mutation batch. This is the "commit" point.

**CheckpointContent:**

```
FormatVersion:              ushort
CheckpointSequence:         ulong       // monotonic counter
FolderTreeRootBlockId:      Ulid
PrimaryIndexRootBlockId:    Ulid
MetadataBlockId:            Ulid
KeyStoreBlockId:            Ulid
PreviousCheckpointBlockId:  Ulid        // chain traversal
FileUUID:                   Guid        // identifies this database instance
LiveBlockCount:             long        // total live blocks at checkpoint time
Timestamp:                  long        // checkpoint creation time
```

**Estimated payload size:** ~130 bytes. Trivial.

**Write protocol:**

1. Write all mutation blocks (email content, BTree nodes, folder pages, etc.)
2. Write updated Metadata block if needed
3. Write Checkpoint block **last** — this commits the batch
4. Anything after the last valid Checkpoint is uncommitted and can be discarded on recovery

**Open protocol (fast):**

1. Seek to end of file
2. Scan backward for the last valid Checkpoint block (look for footer magic, verify checksums)
3. Read Checkpoint — now you have all root pointers
4. Done. No full scan required.

**Fallback:** If no Checkpoint is found (v1 file, or severe corruption), fall back to full sequential scan. This preserves backward compatibility.

**BlockType addition:** `Checkpoint = 11` (shifting proposed folder listing types up by one).

---

## Gap 4: BlockId as Durable Pointer (Not Offsets)

### The Problem

BTree leaf entries currently store `(EmailHashedID, BlockOffset, BlockId)` — 48 bytes. The sync doc already identifies this as hostile to replication: "don't send raw BTree across replicas (offset problem)" (`Sync_and_ULID.md`, Section 6).

Offsets are also compaction-hostile (compaction rewrites them) and corruption-hostile (a corrupt offset is a hard failure).

### Recommendation: Remove Offsets from BTree Leaf Entries

Make BlockId the authoritative reference. Offsets become a runtime-only acceleration structure.

**New leaf entry:** `(EmailHashedID, BlockId)` — 32 + 16 = **48 bytes with ULID**

This is the same size as today's `(EmailHashedID, BlockOffset, BlockId)` at 32 + 8 + 8 = 48 bytes. Leaf density is unchanged. No tree height impact.

**Runtime resolution:**

- Maintain `Dictionary<Ulid, long>` mapping BlockId → file offset
- Built on open from the Checkpoint's block inventory (or incremental scan)
- One dictionary lookup per read — effectively free in memory
- Compaction rebuilds the map rather than rewriting every BTree leaf

**Benefits:**

| Scenario | With Offsets in BTree | With BlockId-Only |
|----------|----------------------|-------------------|
| Compaction | Must rewrite every leaf node | Rebuild offset map only |
| Sync | Offsets invalid on receiver | BlockIds are universal |
| Corruption | Bad offset = hard failure | Rescan to rebuild map |
| Leaf entry size (with ULID) | 56 bytes (32+8+16) | 48 bytes (32+16) |

The leaf entry actually **shrinks** compared to the ULID proposal in the sync doc (which assumed keeping offsets at 56 bytes). This is a strict improvement.

---

## Gap 5: Per-Folder Sync Primitives

### The Problem

The FolderListing doc defines `FolderPageDirectory` with `TotalEmails`, `PageCount`, etc. but no sync primitive. Without one, sync requires diffing full folder state — expensive and error-prone.

### Decision: FolderVersion Counter on FolderPageDirectory

The original proposal called for three IMAP-style fields (FolderEpoch, NextSequenceId, ModSequence). After discussion, these were determined to be over-engineering for the v1 active-to-backup model.

A single `FolderVersion` counter is sufficient:

| Field | Type | Purpose |
|-------|------|---------|
| FolderVersion | ulong | Incremented every time the delta log compiles into folder chain blocks |

**Cost:** 8 bytes per folder.

**Sync protocol:**

1. Backup connects and reports per-folder state: "My Inbox is at FolderVersion 47."
2. Active responds: "I'm at FolderVersion 52. Here are the current pages."
3. Backup replaces its pages wholesale for that folder.

If FolderVersion matches, no transfer is needed for that folder.

**Why not FolderEpoch / NextSequenceId / ModSequence:**

- FolderEpoch (UIDVALIDITY equivalent) is unnecessary — FolderVersion already signals when pages have changed. A full page replacement handles any structural rebuild.
- NextSequenceId (UIDNEXT equivalent) is only useful for per-message incremental sync, which is not needed when the unit of sync is "entire folder pages."
- ModSequence (CONDSTORE equivalent) would enable delta-based sync from the FolderDeltaLog, but the delta log is local-only (see Gap 6). FolderVersion with wholesale page transfer is simpler and sufficient for v1.

**Updated FolderPageDirectory model:**

```
FolderId:           Ulid
FolderVersion:      ulong
TotalEmails:        int
PageSize:           int
PageCount:          int
PrimarySortKey:     byte
DeltaLogBlockId:    Ulid
Pages[]:
  PageNumber:       int
  FirstDateTicks:   long
  LastDateTicks:    long
  PageBlockId:      Ulid
  RecordCount:      int
```

---

## Gap 6: Tombstone/Deletion Mechanism — Converge on One

### The Problem

The sync doc lists three options (sync log, tombstones, BTree diff) and doesn't commit (`Sync_and_ULID.md`, Section 4.1). The FolderListing doc uses a delta log with `Operation=Delete` (`FolderListing.md`, Section 5). These are at risk of becoming two parallel event systems.

### Decision: FolderDeltaLog Is Local-Only; FolderVersion Drives Sync

The FolderDeltaLog exists for **local folder page/chain block maintenance only**. It is not a sync mechanism.

**How the delta log works locally:**

- Mutations (Add, Delete, FlagChange, Move) are appended to the per-folder delta log.
- Periodically, the delta log compiles into updated folder pages/chain blocks. This increments `FolderVersion`.
- Once compiled, delta log entries are consumed. There is no retention window for sync purposes.

**How sync works (v1 active-to-backup):**

- The `FolderVersion` counter on `FolderPageDirectory` is the sole sync primitive for folder state.
- Backup reports its FolderVersion per folder. If stale, active sends the current folder chain blocks wholesale. Backup replaces its pages.
- There is no delta-based folder sync. No per-replica acknowledgment tracking. No `MinRetainedModSequence`.

**What this eliminates:**

- No separate global sync log
- No tombstone block type
- No BTree diffing for deletion detection
- No delta log retention policy for sync
- No `MinRetainedModSequence` field on FolderPageDirectory
- No per-replica acknowledgment tracking

**Multi-machine writes (out of scope for v1):**

Multi-master sync is explicitly out of scope. The recommended pattern for a second active machine: submit mutations (Add/Delete/Move/FlagChange) to the primary via an application-layer **actions channel** (RPC). The primary applies mutations locally; results replicate back through normal Active to Backup replication. The actions channel is not part of the storage format spec.

This eliminates all merge conflicts: the primary is the single writer for folder structure. EmailContent blocks remain conflict-free because they are content-addressed (same hash = idempotent).

---

## Updated Block Header (v2)

Incorporating all changes from Gaps 1-3:

```
+-----------------------------+-------------------+----------------------------------------+
| Field                       | Size (Bytes)      | Description                            |
+-----------------------------+-------------------+----------------------------------------+
| Header Magic                | 8                 | 0xEE411DBBD114EEUL                     |
| Version                     | 2                 | Format version (2 for this layout)     |
| Block Type                  | 1                 | Enum (see Block Types)                 |
| Flags                       | 1                 | Bit field (see Flags Layout)           |
| Payload Encoding            | 1                 | Enum (see Payload Encodings)           |
| Key Epoch                   | 2                 | DEK epoch for encryption (0-65535)     |
| Timestamp                   | 8                 | UTC Ticks (creation)                   |
| Block ID                    | 16                | ULID (128-bit, lexicographically       |
|                             |                   | sortable, monotonic per-process)       |
| Payload Length               | 8                 | Length of payload data                  |
| Header Checksum             | 16                | BLAKE3-128 of all header fields above  |
+-----------------------------+-------------------+----------------------------------------+
| Payload Data                | Variable          | Block content (encoded per Payload     |
|                             |                   | Encoding)                              |
| Payload Checksum            | 16                | BLAKE3-128 of payload data             |
+-----------------------------+-------------------+----------------------------------------+
| Footer Magic                | 8                 | ~HEADER_MAGIC                          |
| Total Block Length           | 8                 | Size of entire block                   |
+-----------------------------+-------------------+----------------------------------------+
```

**Header size:** 47 bytes (was 37)
**Total fixed overhead:** 91 bytes (was 81)
**Net increase:** 10 bytes per block

### Flags Bit Layout

```
Bit 0: Encrypted        (0 = plaintext, 1 = AES-GCM encrypted)
Bit 1: Compressed        (0 = none, 1 = compressed per Payload Encoding)
Bit 2: Tombstone         (0 = live block, 1 = logically deleted)
Bit 3: Checkpoint        (0 = normal block, 1 = checkpoint block)
Bits 4-7: Reserved       (must be 0 for forward compatibility)
```

---

## Updated Block Types (v2)

```
Metadata            = 0
WAL                 = 1
FolderTree          = 2
Folder              = 3
Segment             = 4
Cleanup             = 5
BTreeLeaf           = 6
BTreeInternal       = 7
IndexRoot           = 8
EmailContent        = 9
KeyStore            = 10
Checkpoint          = 11      // NEW: crash consistency + fast open
EmailMetadata       = 12      // Tier 2: full email headers/envelope
FolderPageDirectory = 13      // Page index per folder
FolderPage          = 14      // One page of listing records
FolderDeltaLog      = 15      // Append-only change log per folder

// Reserved for future phases
FTSSegmentMeta      = 16
FTSTermDictionary   = 17
FTSPostingList      = 18
FTSSearchRoot       = 19
BloomFilter         = 20
EmbeddingContent    = 21
VectorIndexNode     = 22
VectorIndexRoot     = 23
```

---

## Updated BTree Leaf Entry (v2)

```
Current (v1):   EmailHashedID(32) + BlockOffset(8) + BlockId(8)   = 48 bytes
Proposed (v2):  EmailHashedID(32) + BlockId(16)                    = 48 bytes
```

Same size. Same leaf density. Offsets resolved at runtime via `Dictionary<Ulid, long>`.

---

## Decision Summary

| # | Change | Recommendation | Overhead |
|---|--------|---------------|----------|
| 1 | 16-byte ULID BlockId | Yes — v2 format, clean break | +8 bytes/block header, +8 bytes/reference |
| 2 | Separate KeyEpoch from Flags | Yes — 2-byte KeyEpoch field, reclaim Flags for booleans | +1 byte/block header |
| 3 | Checkpoint block type | Yes — fast open, crash recovery, sync anchor | ~130 bytes per checkpoint |
| 4 | BlockId-only BTree leaves | Yes — offsets derived at runtime | Net zero (offset removed, ULID added) |
| 5 | Per-folder sync primitives | Yes — FolderVersion counter on FolderPageDirectory | 8 bytes per folder |
| 6 | FolderDeltaLog is local-only; FolderVersion drives sync; actions channel for multi-machine (out of scope) | Yes — no separate sync log, no tombstone type, no delta retention | No additional overhead |

**All six changes are format-level. Bundle into a single v2 format version bump.**
