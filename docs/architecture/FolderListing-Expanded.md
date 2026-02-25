# Folder Listing & Search — Expanded Design (Archival Focus)

**Date:** 2026-02-25
**Builds on:** FolderListing.md (initial research report)
**Status:** Refined design proposal for review

---

## 1. Reframing: Archival Format First

The initial research report (FolderListing.md) analysed the problem from the perspective of a live email system handling constant writes. This expanded design reframes the architecture around EmailDB's actual primary use case: **email archival and retrieval**.

### What This Means

- **Write-once, read-many.** Emails are bulk-imported (from PST, IMAP, mbox, EML files). Incremental additions happen but are not the hot path.
- **Search is the killer feature.** Someone opens a 10-year, 50-million-email archive and needs to find specific emails by sender, date, subject, or content. This must be fast.
- **Reliability is the competitive advantage.** PST files corrupt silently and the whole file becomes unusable. EmailDB's per-block BLAKE3 checksums detect and isolate corruption at the block level — one bad block means one damaged email, not a destroyed archive.
- **Data duplication is acceptable.** A 600 GB archive file is fine. Duplicating listing data across folder metadata and email metadata blocks is a worthwhile tradeoff for read performance.
- **Single-file remains a core requirement.** Easy to move, backup, hand to legal, store on a NAS. Just like PST, but without the rot.

### Where This Beats PST

| Aspect | PST | EmailDB |
|--------|-----|---------|
| Corruption | Silent, whole-file failure, frequent | Per-block BLAKE3, isolated damage, verifiable |
| Search | Full scan, painfully slow at scale | BTree indexes, sub-second structured search |
| Size limits | Degrades badly past 10-20 GB | Designed for hundreds of millions of emails |
| Encryption | Trivially cracked | AES-GCM per block, Argon2id key derivation, epoch rotation |
| Integrity verification | None | Verify any individual block on demand |
| Format | Proprietary, reverse-engineered | Open specification |
| Recovery | scanpst.exe, often fails | Block-level recovery, skip damaged blocks |

---

## 2. Data Model: Paired Blocks + Denormalized Indexes

### The Unchanging Pair: EmailMetadata + EmailContent

Each email is stored as two blocks that form a permanent, immutable pair:

**EmailMetadata block (Tier 2, ~2-8 KB):**
```
EmailHashedID       32 bytes    // The primary key
Subject             string      // Full subject
From                string      // Full sender
To                  string      // All recipients
Cc                  string      // CC recipients
Bcc                 string      // BCC recipients
Date                DateTime    // Email date
MessageId           string      // RFC 5322 Message-ID
InReplyTo           string      // Threading
References          string      // Threading
AttachmentCount     int         // Number of attachments
ContentSize         long        // Size of the content block payload
Flags               byte        // Read/unread, starred, etc. (initial state)
ContentBlockId      Ulid        // Pointer to the paired content block (16-byte ULID)
FolderPath          string      // Folder at time of archival
Snippet             string      // First ~200 chars of body text (for preview)
```

**EmailContent block (Tier 3, variable, 1 KB - 25 MB):**
```
Raw MIME bytes, inline images, attachments — the full email payload.
```

These two blocks are written together during ingest and **never modified**. They are a permanent pair. The `ContentBlockId` in the metadata block points directly to the content block — the file offset is resolved at runtime via `Dictionary<Ulid, long>`, so no BTree lookup is needed to go from metadata to content.

### Why Paired Blocks Work for Archival

In an archival format, emails don't change. You don't mark them as read, you don't move them between folders (the folder structure reflects the state at time of archival). The metadata and content blocks are written once and become permanent records. This means:

- No write amplification from flag changes
- No need for mutable listing structures
- The BTree and folder metadata can be built optimally at the end of a bulk import
- Incremental adds (rare) just append new block pairs and update indexes

---

## 3. Primary BTree: EmailHashedID -> EmailMetadata Location

The existing primary BTree remains largely unchanged:

```
Key:    EmailHashedID (32 bytes)
Value:  MetadataBlockId (16 bytes, ULID)
```

The value now points to the **EmailMetadata block** (not the content block). Offsets are resolved at runtime via `Dictionary<Ulid, long>`. From the metadata block, the `ContentBlockId` field leads directly to the content block. This is a two-hop read to get full email content:

```
Primary BTree lookup -> EmailMetadata block -> EmailContent block
```

This is fine for the "open a specific email" use case. The BTree lookup is 3-4 reads (tree height 4 at 100M emails with branching factor 54), then 1 read for metadata, then 1 read for content. Total: 5-6 reads per email open.

---

## 4. Secondary Indexes: BTree-Based Search

### Design Principle: Composite Keys in the Existing BTree Format

The BTree's `LeafEntry` is fixed at 48 bytes: 32-byte key + 16-byte BlockId (ULID). Offsets resolved at runtime. Secondary indexes use **composite keys** that pack the indexed field into the first bytes of the 32-byte key, with the EmailHashedID suffix for uniqueness.

The value (BlockId) in every secondary index points to the **EmailMetadata block** for that email. This means any search result can immediately load the full metadata without a primary BTree lookup.

### From/To/Cc Index

A single BTree indexing all email addresses with a role flag:

```
Key (32 bytes):
  Bytes 0-14:   BLAKE3(normalized_email_address)[0..14]  // 15-byte address hash prefix
  Byte 15:      Role flag (0x00=From, 0x01=To, 0x02=Cc, 0x03=Bcc)
  Bytes 16-31:  EmailHashedID[0..15]                      // 16-byte uniqueness suffix

Value (16 bytes):
  EmailMetadata BlockId (16 bytes, ULID)
```

**How it works:**

The 15-byte address hash prefix groups all emails involving the same address together in the BTree. The role flag byte sorts From before To before Cc within that group. The EmailHashedID suffix ensures uniqueness (no duplicate keys).

**Queries:**

| Query | Range Scan |
|-------|------------|
| "All emails from alice@example.com" | `[BLAKE3(alice@)[0..14], 0x00, 0x00...] to [BLAKE3(alice@)[0..14], 0x00, 0xFF...]` |
| "All emails to alice@example.com" | Same but role byte = `0x01` |
| "All emails involving alice@" | `[BLAKE3(alice@)[0..14], 0x00, 0x00...] to [BLAKE3(alice@)[0..14], 0x03, 0xFF...]` |

**Write cost at ingest:** One insert per address per email. An email with From + To + 3 Cc = 5 inserts. For bulk import this is part of the index-building phase.

**Collision risk:** 15-byte BLAKE3 prefix = 120 bits. Probability of two different addresses colliding: ~1 in 2^120. Effectively zero. Even at 100 billion unique addresses, collision probability is negligible.

### Date Index

```
Key (32 bytes):
  Bytes 0-7:    (ulong)(DateUtcTicks)     // 8 bytes — natural chronological sort
  Bytes 8-31:   EmailHashedID[0..23]      // 24-byte uniqueness suffix

Value (16 bytes):
  EmailMetadata BlockId (16 bytes, ULID)
```

Since `EmailHashedID.CompareTo` compares `_part1` (bytes 0-7) first as `ulong`, and UTC ticks are positive values that sort correctly as unsigned integers, this gives **chronological ordering natively**.

**Queries:**

| Query | Range Scan |
|-------|------------|
| "Emails from last 7 days" | `[DateTicks(7 days ago), 0x00...] to [DateTicks(now), 0xFF...]` |
| "Emails from 2024" | `[DateTicks(2024-01-01), 0x00...] to [DateTicks(2024-12-31), 0xFF...]` |
| "Emails before March 2020" | `[0x00..., 0x00...] to [DateTicks(2020-03-01), 0xFF...]` |

**For date-descending display:** Reverse the result list in memory. The BTree stores ascending, the caller reverses. Trivial.

### Subject Index (Optional, Phase 2)

For subject keyword search, a BTree isn't ideal (you can't do substring matching in a BTree). Two options:

1. **Scan FolderMetadata chain** — for folder-scoped subject search, load the folder's chain and scan subject strings in memory. At 100K emails this is ~20 MB, scanned in milliseconds.
2. **Inverted index** — for global subject/body search, build a segment-based inverted index during import (Phase 2+). This is the full-text search path.

For the archival use case, option 1 covers most needs. Users searching an archive typically know the folder or can narrow by sender/date first.

### Flags Index

For an archival format, flags largely reflect the state at time of archival (read/unread, flagged, etc.) and don't change afterward. Two approaches:

1. **FolderMetadata contains flags** — filter in memory after loading the folder chain. Simple, fast for folder-scoped queries.
2. **Flags BTree** — only needed for cross-folder queries like "all starred emails in the archive." Key = `[FlagByte, EmailHashedID]`. Optional, low priority.

Recommendation: Start with flags in FolderMetadata only. Add a Flags BTree later if cross-folder flag queries prove necessary.

### Index Summary

| Index | Key Structure | Points To | Built During |
|-------|--------------|-----------|-------------|
| **Primary** | EmailHashedID (32 bytes) | EmailMetadata block | Ingest |
| **From/To/Cc** | AddressHash(15) + Role(1) + EmailID(16) | EmailMetadata block | Ingest |
| **Date** | DateTicks(8) + EmailID(24) | EmailMetadata block | Ingest |
| **Flags** (optional) | FlagByte(1) + EmailID(31) | EmailMetadata block | Ingest |

Each index is its own BTree instance with its own `IndexRoot` block, tracked in `MetadataContent`.

---

## 5. FolderMetadata: Chained Blocks

### The Problem with Individual Metadata Block Reads

If a folder has 100,000 emails and you need to display a listing, you must get the display data (subject, from, date, flags, size) for each email. Reading 100,000 individual EmailMetadata blocks means 100,000 random disk seeks. Even on NVMe, that's seconds of I/O.

### The Solution: FolderMetadata Chain

Each folder has a **chain of FolderMetadata blocks** containing **denormalized listing data** for all emails in that folder. This is intentional data duplication — the same subject/from/date data exists in both the EmailMetadata block and the FolderMetadata chain. The duplication is the point: it means folder listing never touches individual email blocks.

### Chain Structure

```
FolderContent block (the existing folder block, extended):
  FolderId:             long
  ParentFolderId:       long
  Name:                 string
  TotalEmailCount:      int
  FolderVersion:        ulong        // Increments on each delta log flush/compile into chain blocks
  ChainLength:          int          // Number of FolderMeta blocks in chain
  FirstChainBlockId:    Ulid         // Head of the chain (16-byte ULID)
  ChainBlockIds:        Ulid[]       // All chain block IDs (for parallel reads; offsets resolved at runtime)

FolderMeta block (new block type, one per chain link):
  FolderId:             long
  SequenceNumber:       int          // 0, 1, 2, ... position in chain
  RecordCount:          int          // Emails in this block
  NextBlockId:          Ulid         // Nil ULID if last in chain
  Records[]:
    EmailHashedID:      32 bytes
    DateUtcTicks:       8 bytes
    Flags:              4 bytes
    MessageSize:        4 bytes
    SubjectLen:         1 byte
    Subject:            up to 127 bytes
    FromLen:            1 byte
    From:               up to 127 bytes
    ToLen:              1 byte
    To:                 up to 127 bytes
    // ~170 bytes average per record
```

### Sizing

Target: **~5,000 listing records per FolderMeta block**.

At ~170 bytes per record, each block payload is ~850 KB. With the 91-byte block overhead + encryption overhead, each block is ~850 KB on disk.

| Folder Size | Chain Length | Total Chain Size | Reads to Load |
|-------------|-------------|------------------|---------------|
| 500 emails | 1 block | ~85 KB | 1 |
| 5,000 emails | 1 block | ~850 KB | 1 |
| 20,000 emails | 4 blocks | ~3.4 MB | 4 |
| 50,000 emails | 10 blocks | ~8.5 MB | 10 |
| 100,000 emails | 20 blocks | ~17 MB | 20 |
| 500,000 emails | 100 blocks | ~85 MB | 100 |

For 100K emails: **20 block reads** instead of 100,000 individual metadata block reads. That's a 5,000x reduction in I/O operations.

### Read Path: Listing a Folder

```
1. Read FolderContent block                [1 read, from FolderTree cache]
   -> Get ChainBlockIds[] (offsets resolved at runtime via Dictionary<Ulid, long>)

2. Read FolderMeta chain blocks            [N reads, can be parallelized]
   -> For 100K emails: 20 reads of ~850 KB each

3. Merge all records into memory           [fast, sequential scan]
   -> ~17 MB in memory for 100K emails

4. Sort by date descending (or any field)  [in-memory sort, < 50ms]

5. Paginate and return requested page      [instant, array slice]
```

**Total I/O for a 100K-email folder: 21 reads, ~17 MB.** On NVMe, well under 100ms. Then cached — all subsequent sorts, filters, and page views are instant from memory.

### Why the FolderContent Block Stores the Full Chain Directory

The `FolderContent` block stores all `ChainBlockIds[]` upfront (offsets are resolved at runtime via `Dictionary<Ulid, long>`). This means:

- **No chain traversal needed.** You don't have to read block 1 to find block 2 to find block 3. You read the FolderContent, resolve offsets from the in-memory dictionary, and can issue all chain block reads in parallel.
- **The system keeps `FolderContent` locations in memory** (via the FolderTree). Opening any folder is always: 1 cached lookup + N parallel reads.
- **The chain is cacheable.** After first load, the CacheManager holds the full chain. Subsequent access is pure memory.

### Write Path: Bulk Import

During bulk import, FolderMeta chains are built at the end:

```
Phase 1: Write all EmailContent + EmailMetadata block pairs
Phase 2: Build primary BTree (EmailHashedID -> MetadataBlock)
Phase 3: Build secondary indexes (From/To, Date)
Phase 4: For each folder:
           - Collect listing records for all emails in that folder
           - Pack into FolderMeta chain blocks (5,000 per block)
           - Write chain blocks
           - Write FolderContent block with chain directory
Phase 5: Write FolderTree block
Phase 6: Update file Metadata block
```

### Write Path: Incremental Add (Rare)

When adding a few emails to an existing archive:

```
1. Write new EmailContent + EmailMetadata block pair
2. Update primary BTree (COW insert)
3. Update secondary indexes (COW inserts)
4. Read the last FolderMeta block in the chain for the target folder
5. If it has room (< 5,000 records): append record, rewrite that block
   If full: create a new chain block, update FolderContent's chain directory
6. Rewrite FolderContent block with updated chain directory
```

Write amplification for incremental add: rewrite of the last chain block (~850 KB worst case) + FolderContent block. For a rare operation on an archival format, this is negligible.

### Records Are Unsorted Within Chain Blocks

Chain blocks store records in **insertion order, not date order**. Sorting is done in memory after loading. This is deliberate:

- No cascade problem — adding an email touches only the last chain block
- Any sort order (date, subject, sender, size) is equally fast — just a different in-memory sort
- The MinDate/MaxDate fields in FolderContent chain entries allow date-range filtering at the block level if needed (skip blocks that can't contain relevant dates)

---

## 5b. Mutability Model: What Changes and What Doesn't

This is critical to get right. The system has **immutable blocks** and **mutable structures**, and they have different write strategies.

### Immutable (Write-Once, Never Modified)

| Block | Why Immutable |
|-------|---------------|
| EmailContent (Tier 3) | Raw email data. The email itself never changes. |
| EmailMetadata (Tier 2) | Paired with content. Captures the email's headers, snippet, content pointer at ingest time. |

These form a permanent pair. Once written, they are never touched again. All indexes and folder structures point to them, but they are passive data.

### Mutable (Updated Via COW Append)

| Structure | What Changes | How Often |
|-----------|-------------|-----------|
| Primary BTree | New emails added | On every ingest |
| Secondary BTrees (From/To, Date) | New emails added | On every ingest |
| FolderMeta chain blocks | Emails added/moved/deleted from folders | On folder mutations |
| FolderContent block | Chain directory updated | When chain blocks change |
| FolderTree block | Folders created/deleted/renamed | Rare |
| File Metadata block | Index root BlockIds updated | On structural changes |

The mutable structures all use **copy-on-write**: a new version of the block is appended to the file, and the old version becomes dead space reclaimed at compaction.

### The Folder Mutation Problem

While EmailMetadata + EmailContent blocks are truly write-once, **folder membership changes**:

- Restoring emails from archive to a different folder structure
- Reorganizing folders after import
- Moving emails between folders during review
- Deleting emails (moving to Trash/Deleted folder)
- Importing additional emails into existing folders

Each of these touches FolderMeta chain blocks. Rewriting an 850 KB chain block for every single email move would be wasteful if someone is reorganizing hundreds of emails.

---

## 5c. FolderMeta Delta WAL (Transaction Log)

To handle folder mutations efficiently, each folder has an **append-only delta log** that buffers changes until there are enough to justify rebuilding the affected chain blocks.

### Delta Log Structure

```
FolderDeltaLog block (new block type, one per folder, small):
  FolderId:           long
  EntryCount:         int
  Entries[]:
    Operation:        byte
                        0x01 = Add (new email in folder)
                        0x02 = Remove (email moved out / deleted)
                        0x03 = FlagChange (read/unread, starred, etc.)
                        0x04 = Move (email moved in from another folder)
    EmailHashedID:    32 bytes
    ListingRecord:    ~170 bytes (present for Add/Move/FlagChange, omitted for Remove)
```

### How It Works

**Mutation (move email from Inbox to Archive):**

```
1. Append to Inbox's delta log:  {Remove, EmailHashedID}         [~33 bytes]
2. Append to Archive's delta log: {Move, EmailHashedID, Record}  [~203 bytes]
3. Done. No chain blocks rewritten.
```

**Reading a folder (with pending deltas):**

```
1. Read FolderContent -> get chain directory + DeltaLogBlockId
2. Read FolderMeta chain blocks (the snapshot)
3. Read FolderDeltaLog block
4. Merge in memory:
   - Apply Adds: insert new records
   - Apply Removes: exclude those EmailHashedIDs
   - Apply FlagChanges: update flags on matching records
   - Apply Moves: insert records (same as Add, but semantically from another folder)
5. Sort, filter, paginate as normal
```

**Delta log flush (rebuild chain):**

When the delta log exceeds a threshold (configurable, e.g., 500 entries or ~100 KB):

```
1. Read all chain blocks + delta log
2. Apply all deltas to produce the merged record set
3. Re-pack into new chain blocks (5,000 per block)
4. Write new chain blocks
5. Write updated FolderContent with new chain directory, DeltaLogBlockId = Nil ULID, FolderVersion incremented
6. Old chain blocks + old delta log block -> outdated (compaction reclaims)
```

This can happen:
- Explicitly (user triggers "optimize archive")
- During compaction
- Automatically when delta log exceeds threshold
- On next folder open if the delta log is large

### Why This Matters

Without the delta log, moving 500 emails between folders means 500 chain block rewrites (potentially 500 x 850 KB = 425 MB of writes). With the delta log, it's 500 x ~200 bytes = ~100 KB of appends, followed by a single chain rebuild when convenient.

The delta log is especially important for the **initial organisation phase** after a bulk import — the user opens the archive, reviews the folder structure, and moves batches of emails around. The delta log absorbs all of this cheaply.

### FolderContent Extension for Delta Log

```
FolderContent (extended):
  FolderId:             long
  ParentFolderId:       long
  Name:                 string
  TotalEmailCount:      int
  FolderVersion:        ulong        // Increments on each delta log flush/compile into chain blocks
  ChainLength:          int
  DeltaLogBlockId:      Ulid         // Nil ULID if no pending deltas
  DeltaEntryCount:      int          // 0 if no pending deltas
  ChainEntries[]:
    BlockId:            Ulid         // 16-byte ULID (offset resolved at runtime)
    RecordCount:        int
    MinDateTicks:       long
    MaxDateTicks:       long
```

> **Note:** Delta logs are **local-only** -- they exist for folder chain block maintenance, not for sync/replication. When the delta log is flushed into chain blocks, the `FolderVersion` on FolderContent increments. Replicas use `FolderVersion` to determine if they need updated chain blocks.

---

## 6. Memory Model and Caching

### What Lives in Memory

| Data | When Loaded | Size at 100M Emails | Eviction |
|------|-------------|---------------------|----------|
| FolderTree | File open | ~50 KB (folder structure) | Never — pinned |
| FolderContent blocks | Folder opened | ~1-5 KB per folder | LRU, keep active folders |
| BTree top 3 levels | File open | ~12 MB (2,971 nodes x 4 KB) | Never — pinned |
| FolderMeta chain (active folder) | Folder listed | ~17 MB per 100K-email folder | LRU, keep 2-3 hot folders |
| EmailMetadata block | Email opened | ~4 KB per email | LRU, keep last ~500 |
| EmailContent block | Email viewed | Variable | Evict immediately after display |

**Typical working memory:** ~50 MB for an active session browsing a large archive. This covers the BTree top levels, one or two folder chains, and recently opened email metadata.

### Startup Sequence

In v2, the file uses a **Checkpoint block** for fast startup. The startup sequence seeks to the end of the file and scans backward for the last valid Checkpoint block, which contains all root pointers and offsets needed to bootstrap the system. No full sequential scan is required.

```
1. Seek to end of file, scan backward for last valid Checkpoint block
2. Read Checkpoint block                       -> Get all root pointers and offsets
3. Read FolderTree block                       -> Folder hierarchy in memory
4. Read primary BTree IndexRoot                -> Root BlockId in memory
5. Read secondary index IndexRoots             -> Root BlockIds in memory
6. Pre-cache BTree top 2-3 levels              -> ~12 MB, fast subsequent lookups

Ready. Total startup reads: ~10-15 blocks. Sub-second on NVMe.
```

> **Fallback:** For v1 files or if the Checkpoint is corrupted, the system falls back to a full sequential scan: read the Header block at offset 0, then the Metadata block, and rebuild root pointers from there.

---

## 7. Search Workflows at Scale

### Scenario: 100 Million Emails, 600 GB Archive

**"Find all emails from john.smith@acme.com"**

```
1. From/To BTree range scan on BLAKE3(john.smith@acme.com) prefix
2. Tree height ~5 at 100M entries (branching factor 54)
3. Navigate to first matching leaf: 5 reads
4. Scan forward through matching leaves: 1 read per ~82 results
5. If John sent 500 emails: 5 + 7 = 12 reads total
6. Each leaf entry points to EmailMetadata block
7. Load metadata for display: 500 reads (or use FolderMeta if folder-scoped)

Total: ~512 reads. Sub-second on NVMe.
```

**"Find all emails from March 2024"**

```
1. Date BTree range scan: [2024-03-01, 2024-03-31]
2. Navigate to start of range: 5 reads
3. Scan forward: depends on volume. 10,000 emails in March = ~122 leaf reads
4. Total: ~127 reads for the IDs
5. Display results: load from FolderMeta chains or EmailMetadata blocks

Total: ~130+ reads. Fast for ID retrieval, display depends on result count.
```

**"List the Inbox folder" (50,000 emails)**

```
1. FolderContent from cache: 0 reads (already in memory from FolderTree)
2. Load FolderMeta chain: 10 blocks x ~850 KB = ~8.5 MB
3. Sort by date in memory: < 20ms
4. Display page 1: instant (array slice)

Total: 10 reads on first load, then cached. < 50ms.
```

**"Search subject for 'invoice' within Inbox"**

```
1. FolderMeta chain already cached from listing (0 reads)
2. Scan 50,000 subject strings in memory: < 5ms
3. Filter and return matches: instant

Total: 0 reads if folder already loaded, ~10 reads if cold.
```

**Combined: "Emails from alice@ in the last 30 days with 'project' in subject"**

```
1. Date BTree range scan (last 30 days): ~50-100 reads for candidate EmailHashedIDs
2. From/To BTree range scan (alice@): ~10-20 reads for candidate EmailHashedIDs
3. Intersect the two sets in memory: instant
4. Load EmailMetadata for intersection set: N reads (or check FolderMeta cache)
5. Filter by subject substring in memory: instant

Total: ~70-120 reads for the index lookups + metadata loads.
```

---

## 8. New Block Types

```
Existing:
  Metadata        = 0
  WAL             = 1
  FolderTree      = 2
  Folder          = 3      // FolderContent — extended with chain directory
  Segment         = 4
  Cleanup         = 5
  BTreeLeaf       = 6
  BTreeInternal   = 7
  IndexRoot       = 8
  EmailContent    = 9      // Tier 3 — unchanged
  KeyStore        = 10

New:
  Checkpoint      = 11     // Fast startup — root pointers, written periodically
  EmailMetadata   = 12     // Tier 2 — paired with EmailContent
  FolderMeta      = 13     // Chain blocks for folder listing data
  FolderDeltaLog  = 14     // Append-only mutation log per folder

  // Reserved for future phases:
  FTSSegmentMeta  = 15     // Full-text search segment metadata
  FTSTermDict     = 16     // Inverted index term dictionary
  FTSPostingList  = 17     // Inverted index posting lists
  FTSSearchRoot   = 18     // FTS index root
  BloomFilter     = 19     // Per-segment bloom filters

  // Semantic search (separate, re-generable layer):
  EmbeddingBlock  = 20     // Vector embeddings per email
  EmbeddingIndex  = 21     // ANN index nodes (HNSW/IVF)
  EmbeddingRoot   = 22     // Embedding index root + model metadata
```

### MetadataContent Extensions

```
Existing fields:
  WALBlockId                Ulid          // (offset resolved at runtime)
  FolderTreeBlockId         Ulid          // (offset resolved at runtime)
  SegmentBlockIds           Dictionary<string, Ulid>
  OutdatedBlockIds          List<Ulid>

New fields:
  PrimaryIndexRootBlockId   Ulid          // Existing primary BTree root (Nil ULID = absent)
  FromToIndexRootBlockId    Ulid          // From/To/Cc secondary BTree root
  DateIndexRootBlockId      Ulid          // Date secondary BTree root
  FlagsIndexRootBlockId     Ulid          // Flags secondary BTree root (optional)
  FTSSearchRootBlockId      Ulid          // Full-text search root (future)
  EmbeddingRootBlockId      Ulid          // Semantic embedding root (Nil ULID = not generated)
  TotalEmailCount           long = 0      // Global email count
  ArchiveCreatedDate        DateTime      // When the archive was created
  ArchiveSourceType         string        // "PST", "IMAP", "MBOX", etc.
```

---

## 9. FolderContent Block — Extended Design

The existing `FolderContent` block is extended to serve as the **chain directory** for FolderMeta blocks:

```
FolderContent (extended):
  FolderId:             long
  ParentFolderId:       long
  Name:                 string          // Folder path
  TotalEmailCount:      int             // Total emails in this folder
  FolderVersion:        ulong           // Increments on each delta log flush/compile
  ChainLength:          int             // Number of FolderMeta blocks
  ChainEntries[]:                       // One per FolderMeta block in chain
    BlockId:            Ulid            // FolderMeta block ID (16-byte ULID; offset resolved at runtime)
    RecordCount:        int             // Emails in this chain block
    MinDateTicks:       long            // Earliest email date in block
    MaxDateTicks:       long            // Latest email date in block
```

The `ChainEntries[]` array serves multiple purposes:
- **Parallel reads:** All BlockIds known upfront, offsets resolved at runtime via `Dictionary<Ulid, long>`, read all chain blocks simultaneously
- **Date-range skipping:** If the user filters by date, skip chain blocks whose min/max dates don't overlap the query range
- **Progressive loading:** Load just the first chain block for an initial view, then load remaining blocks in the background

The existing `List<EmailHashedID> EmailIds` field can be **removed or retained as a lightweight membership set**. The FolderMeta chain now carries the authoritative listing data. If EmailIds is retained, it serves as a backup for chain reconstruction.

---

## 10. Bulk Import Pipeline

The optimal import sequence for building an archive:

```
Pass 1 — Write Email Block Pairs:
  For each email:
    1. Parse email (MIME, headers, body)
    2. Compute EmailHashedID
    3. Write EmailContent block (Tier 3) -> get contentBlockId (Ulid)
    4. Build EmailMetadataContent with all fields + content pointer
    5. Write EmailMetadata block (Tier 2) -> get metadataBlockId (Ulid)
    6. Buffer: {EmailHashedID -> metadataBlockId} for BTree
    7. Buffer: {address -> (EmailHashedID, role)} for From/To index
    8. Buffer: {dateTicks -> EmailHashedID} for Date index
    9. Buffer: {folderPath -> listingRecord} for FolderMeta chains

Pass 2 — Build Primary BTree:
  Sort buffered entries by EmailHashedID
  Bulk-insert into BTree (bottom-up leaf construction is faster than individual inserts)

Pass 3 — Build Secondary Indexes:
  For each secondary index (From/To, Date):
    Sort buffered entries by composite key
    Bulk-insert into separate BTree instance

Pass 4 — Build FolderMeta Chains:
  For each folder:
    Take buffered listing records
    Pack into FolderMeta blocks (5,000 records per block)
    Write chain blocks (BlockIds assigned as ULIDs)
    Write FolderContent block with chain directory

Pass 5 — Write System Blocks:
  Write FolderTree block (folder hierarchy)
  Write Metadata block (all index root BlockIds, folder tree BlockId)
  Write Checkpoint block (root pointers for fast startup)
```

### Bulk Import Performance Estimate (100M emails, ~600 GB)

| Phase | Work | Estimated Time |
|-------|------|----------------|
| Pass 1: Write block pairs | 200M block writes (~600 GB) | Disk-bound, ~30-60 min on NVMe |
| Pass 2: Primary BTree | 100M inserts | ~10-20 min |
| Pass 3: Secondary indexes | ~300M composite inserts (avg 3 addresses/email) | ~30-60 min |
| Pass 4: FolderMeta chains | Depends on folder count, ~100M records total | ~5-10 min |
| Pass 5: System blocks | A few block writes | Seconds |
| **Total** | | **~1.5-3 hours for 100M emails** |

This is a one-time cost. Once built, the archive is ready for instant browsing and searching.

---

## 11. Incremental Add Path

For adding a small number of emails to an existing archive:

```
1. Write EmailContent + EmailMetadata block pair           [append]
2. Primary BTree: COW insert                               [3-5 block writes]
3. From/To index: COW inserts (1 per address)              [3-5 writes per address]
4. Date index: COW insert                                  [3-5 block writes]
5. Target folder's FolderDeltaLog:
   a. Append {Add, EmailHashedID, ListingRecord}           [~203 bytes]
   b. If delta log exceeds threshold: trigger chain rebuild
6. Done. FolderMeta chain blocks untouched.
```

Total extra writes beyond the email blocks themselves: ~15-25 BTree block writes + ~203 bytes delta log append. The chain blocks are only rebuilt when the delta log is flushed.

### Moving Emails Between Folders

```
1. Append {Remove, EmailHashedID} to source folder's delta log    [~33 bytes]
2. Append {Move, EmailHashedID, ListingRecord} to target's delta  [~203 bytes]
3. Done. No chain blocks, no BTree mutations, no metadata block rewrites.
```

Moving 500 emails: ~118 KB of delta log appends total. Compare to rewriting chain blocks: potentially hundreds of MB.

---

## 12. Compaction Considerations

### What Accumulates Dead Blocks

| Source | Frequency | Dead Block Volume |
|--------|-----------|-------------------|
| BTree COW path rewrites (incremental adds) | Per insert | ~4 blocks per insert per index |
| FolderMeta chain block rewrites | Per folder affected by add | ~850 KB per affected folder |
| FolderContent rewrites | Per folder affected by add | ~1-5 KB per affected folder |

For an archival format, compaction is needed only after significant incremental additions. A fresh bulk import produces zero dead blocks.

### Compaction Strategy

1. **Track dead blocks** in `MetadataContent.OutdatedBlockIds` (existing mechanism)
2. **Trigger compaction** when dead space exceeds a threshold (e.g., 20% of file size)
3. **Full rewrite:** Copy all live blocks to a new file, rebuild in optimal order:
   - System blocks first (Header, Metadata, KeyStore)
   - BTree nodes (optimal layout for traversal)
   - FolderMeta chains (grouped by folder for locality)
   - Email block pairs (sequential)
4. **Swap files** atomically

This is identical to the existing compaction model, extended to handle the new block types.

---

## 13. Encryption Policy for New Block Types

| Block Type | Encrypt? | Rationale |
|------------|----------|-----------|
| EmailMetadata (12) | Yes | Contains subject, sender, recipient data |
| FolderMeta (13) | Yes | Contains denormalized email listing data |
| Secondary BTree nodes | No | Keys are BLAKE3 hashes (opaque). Follows existing BTree policy |
| FTS blocks (future) | Yes | Term dictionaries expose search terms |
| BloomFilter (future) | No | Opaque bit arrays, minimal information leakage |

Secondary index BTree keys are cryptographic hashes of the indexed values, not the plaintext values themselves. An attacker seeing `BLAKE3(alice@example.com)[0..14]` cannot recover the email address. This follows the existing precedent where primary BTree nodes (BTreeLeaf, BTreeInternal, IndexRoot) are unencrypted.

### v2 Block Header Encryption Fields

In v2, `KeyEpoch` is a separate 2-byte field (range 0-65535), no longer packed into Flags bits 1-7. The Flags byte has defined bit assignments:

| Bit | Meaning |
|-----|---------|
| 0 | Encrypted |
| 1 | Compressed |
| 2 | Tombstone |
| 3 | Checkpoint |
| 4-7 | Reserved |

The header grew from 37 to 47 bytes to accommodate the ULID BlockId (16 bytes vs. 8-byte long) and the separate KeyEpoch field. Fixed block overhead is now 91 bytes.

---

## 14. Integrity and Recovery

### Per-Block Integrity

Every block (including FolderMeta chain blocks) has:
- **Header checksum:** BLAKE3-128 of the 47-byte header
- **Payload checksum:** BLAKE3-128 of the payload data
- **Footer magic:** Validates block boundary

If a FolderMeta chain block is corrupted:
1. The checksum failure is detected on read
2. The chain can be **reconstructed** from individual EmailMetadata blocks + the FolderContent's EmailIds list (if retained)
3. Only the corrupted chain block needs rebuilding, not the entire folder

This is a massive advantage over PST, where corruption in the internal B-tree can make entire folder subtrees inaccessible.

### Archive Verification

A verification tool can scan the entire archive:
```
For each block in file:
  1. Verify header checksum
  2. Verify payload checksum
  3. Verify footer magic and length
  4. Report: block type, status (OK/CORRUPT), offset

For each BTree:
  1. Verify Merkle hash chain from root to leaves
  2. Report any broken chains

For each FolderMeta chain:
  1. Verify all chain blocks readable
  2. Cross-reference with FolderContent chain directory
  3. Report any missing or corrupt chain links
```

---

## 15. Semantic Search: A Separate, Re-generable Layer

### Why Semantic Search Is Different From Everything Else

Every other data structure in EmailDB (BTrees, FolderMeta chains, EmailMetadata blocks) is **derived from the email data itself** and uses stable, deterministic algorithms (BLAKE3 hashing, Protobuf serialization, UTC tick values). These will produce the same results forever.

Semantic embeddings are fundamentally different:

- **The embedding model will change.** Today's model might be `text-embedding-3-small` (1536 dimensions). Next year a better model ships with 1024 dimensions and superior retrieval quality. The embeddings need to be **regenerated** without touching any other data.
- **Embeddings are not deterministic across model versions.** The same email will produce different vectors with different models.
- **Embedding dimensions vary.** 768 floats, 1024 floats, 1536 floats — the storage format must handle this.
- **The index structure depends on the embeddings.** An HNSW index built for 1536-dimensional vectors is useless after re-embedding with a 1024-dimensional model. The entire index must be rebuilt.

This means semantic search is a **separate layer that sits alongside the core storage**, not woven into it.

### Design: Embeddings as a Disposable Overlay

```
EmbeddingRoot block:
  ModelIdentifier:      string      // "text-embedding-3-small", "nomic-embed-text-v2", etc.
  ModelVersion:         string      // Version tag for reproducibility
  Dimensions:           int         // Vector dimensionality (768, 1024, 1536)
  Quantization:         byte        // 0=float32, 1=float16, 2=int8
  TotalEmbeddings:      long        // Number of emails embedded
  EmbeddingCreatedDate: DateTime    // When this embedding set was generated
  IndexType:            byte        // 0=HNSW, 1=IVF, 2=brute-force
  IndexRootBlockId:     Ulid        // Points to EmbeddingIndex root node (16-byte ULID)
  EmbeddingBlockIds:    Ulid[]      // All EmbeddingBlock IDs for sequential scan

EmbeddingBlock (one per batch of emails):
  BatchSize:            int
  Entries[]:
    EmailHashedID:      32 bytes                    // Links back to the email
    Vector:             Dimensions × sizeof(type)   // The embedding vector
  // At 1536 × 4 bytes = 6 KB per email, float32
  // At 1536 × 1 byte  = 1.5 KB per email, int8 quantized
  // ~100 emails per block at float32 = ~600 KB per block

EmbeddingIndex blocks:
  HNSW graph layers or IVF centroids, stored as blocks.
  Structure depends on IndexType.
```

### Embedding Generation Pipeline

Embedding generation is a **background process** that runs after the archive is built (or incrementally as emails are added):

```
1. Iterate all EmailMetadata blocks (or scan FolderMeta chains for EmailHashedIDs)
2. For each email:
   a. Read EmailMetadata block -> get subject, from, to, snippet
   b. Optionally read EmailContent block -> get full body text
   c. Construct text for embedding: subject + from + to + snippet (or full body)
   d. Call embedding model API (or local model)
   e. Buffer the (EmailHashedID, vector) pair
3. Batch-write EmbeddingBlock blocks (100 emails per block)
4. Build ANN index (HNSW or IVF) from all vectors
5. Write EmbeddingIndex blocks
6. Write EmbeddingRoot block
7. Update file MetadataContent with EmbeddingRootBlockId
```

### Re-embedding (Model Upgrade)

When a better embedding model becomes available:

```
1. Mark all existing EmbeddingBlock/EmbeddingIndex blocks as outdated
2. Run the embedding generation pipeline with the new model
3. Write new EmbeddingRoot with updated ModelIdentifier/Version/Dimensions
4. Old embedding blocks reclaimed at next compaction
```

**Nothing else in the file changes.** The email blocks, BTree indexes, FolderMeta chains, secondary indexes — all untouched. The embeddings are a pure overlay.

### Storage Cost

| Emails | Dimensions | Precision | Embedding Size | Index Overhead | Total |
|--------|-----------|-----------|----------------|----------------|-------|
| 10M | 1536 | float32 | ~57 GB | ~10-15 GB | ~70 GB |
| 10M | 1536 | int8 | ~14 GB | ~3-5 GB | ~18 GB |
| 10M | 1024 | int8 | ~10 GB | ~2-3 GB | ~12 GB |
| 100M | 1536 | int8 | ~143 GB | ~30 GB | ~170 GB |

For a 600 GB archive, int8-quantized embeddings add ~18 GB (10M emails) to ~170 GB (100M emails). Significant but manageable given the file size expectations.

### Semantic Search Query Flow

```
User: "emails about the quarterly budget review"

1. Embed the query string using the same model        [~50ms API call]
2. ANN search in EmbeddingIndex: top 100 candidates   [~5-20ms]
3. Each candidate has an EmailHashedID
4. Look up display data:
   a. If user is in a folder view: check FolderMeta cache for those IDs
   b. Otherwise: load EmailMetadata blocks for the top results
5. Optionally re-rank with BM25 on subject/body text  [in-memory]
6. Return ranked results with subject, from, date, snippet
```

### Hybrid Search: Combining Structured + Semantic

The most powerful queries combine structured filters (which are fast and precise) with semantic ranking (which understands intent):

```
User: "project delay emails from the engineering team this quarter"

Structured phase (fast, precise):
  1. Date BTree: range scan for this quarter -> candidate set A
  2. From/To BTree: scan for engineering team addresses -> candidate set B
  3. Intersect A and B -> candidate set C (maybe 200 emails)

Semantic phase (intent-aware, on the small candidate set):
  4. Embed "project delay" -> query vector
  5. Compute cosine similarity against embeddings for set C only
  6. Rank by similarity score
  7. Return top 20

Total: ~10 BTree reads + ~200 vector comparisons (in-memory) = near-instant.
```

This pattern — **filter first with indexes, rank second with embeddings** — is how production search systems (Elasticsearch + vector search, Pinecone hybrid, etc.) work. The structured indexes do the heavy lifting of narrowing the candidate set, and the semantic search provides intelligent ranking on the small remainder.

### MetadataContent Extension for Embeddings

```
New field:
  EmbeddingRootBlockId    Ulid         // Nil ULID if no embeddings generated yet
```

When `EmbeddingRootBlockId` is a Nil ULID, semantic search is unavailable. The UI can show "Generate semantic index" as an optional action. This keeps the core archive functional without requiring embedding generation.

---

## 16. Summary: The Complete Data Flow

```
                         ┌─────────────────────────┐
                         │     File Metadata        │
                         │  (index roots, BlockIds) │
                         └───────────┬─────────────┘
                                     │
          ┌──────────────────────────┼──────────────────────────┐
          │                          │                          │
    ┌─────▼─────┐          ┌────────▼────────┐       ┌────────▼────────┐
    │  Primary   │          │   From/To/Cc    │       │     Date        │
    │   BTree    │          │     BTree       │       │     BTree       │
    │            │          │                 │       │                 │
    │EmailHashID │          │AddrHash+Role    │       │DateTicks        │
    │  -> Meta   │          │+EmailID -> Meta │       │+EmailID -> Meta │
    │  BlockId   │          │   BlockId       │       │   BlockId       │
    └─────┬──────┘          └────────┬────────┘       └────────┬────────┘
          │                          │                          │
          │              ┌───────────▼──────────────────────────▼──┐
          │              │                                         │
          └─────────────►│     EmailMetadata Block (Tier 2)        │
                         │  Subject, From, To, Date, Snippet,      │  IMMUTABLE
                         │  Flags, Size, ContentBlockId, ...        │  PAIR
                         │                                         │
                         └──────────────┬──────────────────────────┘
                                        │ ContentBlockId (Ulid)
                                        ▼
                         ┌─────────────────────────────────────────┐
                         │     EmailContent Block (Tier 3)         │
                         │     Raw MIME, attachments                │
                         └─────────────────────────────────────────┘


    ┌──────────────┐      ┌───────────────────┐
    │  FolderTree  │─────►│  FolderContent    │
    │  (hierarchy) │      │  (chain dir +     │
    └──────────────┘      │   delta log ptr)  │
                          └────────┬──────────┘
                                   │
                    ┌──────────────┼──────────────────┐
                    │              │                   │
                    ▼              ▼                   ▼
             ┌────────────┐┌────────────┐      ┌─────────────┐
             │FolderMeta 0││FolderMeta 1│ ...  │FolderDelta  │
             │~5000 emails││~5000 emails│      │Log (pending │
             │            ││            │      │ mutations)  │
             └────────────┘└────────────┘      └─────────────┘
                  SEALED (rebuilt on flush)       APPEND-ONLY


    ┌─────────────────────────────────────────────────────────┐
    │              Semantic Embedding Layer                    │
    │                  (SEPARATE, RE-GENERABLE)                │
    │                                                         │
    │  ┌───────────────┐   ┌────────────┐  ┌──────────────┐  │
    │  │ EmbeddingRoot │──►│ Embedding  │  │ Embedding    │  │
    │  │ (model info,  │   │ Index      │  │ Blocks       │  │
    │  │  dimensions)  │   │ (HNSW/IVF) │  │ (vectors per │  │
    │  └───────────────┘   └────────────┘  │  email)      │  │
    │                                      └──────────────┘  │
    │  Can be regenerated with a new model without touching   │
    │  any other data in the file.                            │
    └─────────────────────────────────────────────────────────┘
```

### Three Access Paths to Email Data

| Path | Use Case | I/O Pattern |
|------|----------|-------------|
| **By identity** (BTree lookup) | Search results, "open this email" | 3-5 reads (tree traversal) -> EmailMetadata -> EmailContent |
| **By folder** (FolderMeta chain + delta log) | "Show me this folder's contents" | 1 FolderContent + N chain blocks + 1 delta log, sorted in memory |
| **By meaning** (Semantic search) | "Find emails about the budget review" | Embed query -> ANN search -> EmailHashedIDs -> display via path 1 or 2 |

All three paths lead to the same emails through different access patterns. The data duplication between EmailMetadata blocks and FolderMeta chains is the price paid for making folder listing fast without per-email block reads.

### Mutability Summary

| Data | Mutability | Write Strategy |
|------|-----------|----------------|
| EmailMetadata + EmailContent | **Immutable** | Written once at ingest, never modified |
| BTree indexes (Primary, From/To, Date) | **Append-only COW** | New versions appended, old nodes become dead space |
| FolderMeta chain blocks | **Sealed snapshots** | Rebuilt when delta log is flushed |
| FolderDeltaLog | **Append-only** | Absorbs all folder mutations cheaply |
| Semantic embeddings | **Disposable overlay** | Regenerated entirely when model changes |
