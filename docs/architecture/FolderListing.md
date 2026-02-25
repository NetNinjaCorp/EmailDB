# Folder Listing, Metadata Storage & Search at Scale

## Research Report -- EmailDB Architecture Review

**Date:** 2026-02-25
**Scope:** How to handle folder listing, email metadata storage, and search efficiently at 10M+ emails
**Status:** Proposal for review

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [The Problem Quantified](#2-the-problem-quantified)
3. [How Production Email Systems Handle This](#3-how-production-email-systems-handle-this)
4. [Recommended Architecture: Three-Tier Storage](#4-recommended-architecture-three-tier-storage)
5. [Folder Listing: Paginated Pages + Delta WAL](#5-folder-listing-paginated-pages--delta-wal)
6. [Search Strategy](#6-search-strategy)
7. [Encryption Considerations](#7-encryption-considerations)
8. [Compaction & Maintenance](#8-compaction--maintenance)
9. [Cache Architecture](#9-cache-architecture)
10. [Approaches Considered & Rejected](#10-approaches-considered--rejected)
11. [New Block Types & Models](#11-new-block-types--models)
12. [Migration Path](#12-migration-path)
13. [Phased Implementation Plan](#13-phased-implementation-plan)

---

## 1. Executive Summary

The current EmailDB architecture stores all email data (headers, body, attachments) in a single block type and relies on per-email BTree lookups to serve folder listings. At 10M emails with folders containing 50,000+ messages, this requires **200,000+ random disk reads** just to render a folder view -- completely unworkable.

Every production email system that handles large folders uses the same core strategy: **separate listing metadata from full email content, and store it in a compact, sequentially-readable structure per folder.** Dovecot, Cyrus, Thunderbird, and Apple Mail all do this.

**This report recommends three major architectural changes:**

1. **Three-tier data separation** -- Split email storage into Listing (hot, ~200 bytes), Metadata (warm, ~4 KB), and Content (cold, variable) blocks
2. **Paginated folder pages + delta WAL** -- Pre-sorted, pre-paginated listing pages per folder with an append-only change log, enabling 2-3 block reads for any folder page
3. **Phased search** -- Secondary BTree indexes for structured queries (sender, date), with segment-based inverted indexes for full-text body search in a later phase

These changes reduce folder listing from 200,000+ random reads to **2-3 sequential reads**, while keeping write amplification minimal and working naturally with the existing encryption, checksum, and compaction infrastructure.

---

## 2. The Problem Quantified

### Current Data Path for "List Folder Inbox"

```
FolderManager.GetEmailsAsync("Inbox")
  -> CacheManager.GetCachedFolder("Inbox")
    -> Returns FolderContent with List<EmailHashedID>  (just IDs, no display data)
```

`FolderContent` stores only a flat list of `EmailHashedID` values (32 bytes each). To display a folder listing, you then need the display metadata for each email, which currently lives inside `EnhancedEmailContent` -- the **full email** including body and attachments.

### Cost at Scale (50,000-email folder)

| Operation | Count | I/O Type | Estimated Cost |
|-----------|-------|----------|----------------|
| Read FolderContent block | 1 | Sequential (~1.6 MB for 50K x 32-byte IDs) | ~1-5ms SSD |
| BTree lookups (height 3-4) | 50,000 | Random (3-4 reads each) | 150,000-200,000 reads |
| Email content block reads | 50,000 | Random (variable size) | 50,000 reads |
| **Total random I/O** | | | **200,000-250,000 seeks** |

Even on NVMe at ~10us per random 4K read: **2-2.5 seconds** of raw I/O. On spinning disk: **50+ seconds**. This ignores deserialization, decryption, and memory allocation.

### The Root Cause

The display metadata for folder listing is co-located with the full email content. Reading a subject line requires decrypting and deserializing a block that may contain megabytes of attachments.

---

## 3. How Production Email Systems Handle This

### Dovecot (Gold Standard for IMAP)

Three separate per-mailbox files:
- **`dovecot.index`** -- Fixed-size per-message records (UID, flags). Mmap'd, array-indexed O(1)
- **`dovecot.index.log`** -- Append-only transaction log of changes since last rebuild
- **`dovecot.index.cache`** -- Variable-sized cached headers/envelope/snippets, accessed by offset

Opening a 50,000-message folder: mmap the index (few hundred KB), read cache entries for the visible page. Effectively 2-3 reads.

### Cyrus IMAP

Per-mailbox `cyrus.index` with fixed-size records containing UID, INTERNALDATE, flags, size, and a cache offset. Folder listing by date or size served entirely from the index file without touching message content.

### Thunderbird

- **Mbox files** for raw content (sequential flat file per folder)
- **`.msf` summary files** with cached listing metadata (subject, from, date, flags, mbox offset)
- **Gloda SQLite DB** for cross-folder search

The `.msf` file is exactly a "folder summary block" -- denormalized listing data maintained alongside content.

### Apple Mail

SQLite database (`Envelope Index`) with normalized tables for messages, addresses, subjects, recipients. Folder listing is a SQL query filtered by mailbox_id.

### Notmuch

Xapian full-text index with tags as metadata. `folder:Inbox` is a search query. Flexible but slow for large result sets.

### Universal Pattern

Every system separates listing metadata from content. The variations are in structure (fixed-size array vs. B-tree vs. SQL) and update strategy (in-place vs. append-only vs. periodic rebuild).

---

## 4. Recommended Architecture: Three-Tier Storage

### The Three Tiers

Instead of storing everything in a single `EmailContent` block, split into three tiers based on access temperature:

#### Tier 1 -- EmailListing (Hot)

What you need to render one row in a folder view. Read constantly.

| Field | Size | Purpose |
|-------|------|---------|
| EmailHashedID | 32 bytes | Unique key |
| DateUtcTicks | 8 bytes | Sort key, display date |
| Flags | 4 bytes | Read/unread, flagged, replied, forwarded, has-attachment, draft |
| MessageSize | 4 bytes | Display "2.3 MB" in UI |
| Subject | ~128 bytes (variable, capped) | Display in listing |
| From | ~128 bytes (variable, capped) | Display sender |
| To (first recipient) | ~128 bytes (variable, capped) | For "Sent" folder display |
| **Total** | **~200-330 bytes** | |

At 10M emails: **~2-3 GB** of listing data total across all folders.

#### Tier 2 -- EmailMetadata (Warm)

Full header/envelope data. Read when opening a specific email.

- All RFC 5322 headers (To, Cc, Bcc, Reply-To, X-headers)
- MIME structure tree (part types, sizes, filenames) without content
- Message-ID, In-Reply-To, References (for threading)
- Content preview / snippet (~200 chars of body text)
- ContentBlockId -- pointer to the Tier 3 block
- ContentSize -- size of full content payload

**Typical size: 2-8 KB per email.** Individual blocks, one per email.

#### Tier 3 -- EmailContent (Cold)

The existing `BlockType.EmailContent`. Raw MIME body, inline images, attachments. Read only when the user opens an email and wants to see the body/attachments.

**Typical size: 10 KB to 25 MB per email.**

### Why Three Tiers, Not Two

Two tiers (metadata + content) are insufficient because the metadata needed for a folder listing (~200 bytes) is a strict subset of full metadata (~4 KB). At 50,000 emails per folder page request:

| Approach | Data per Page (50 emails) | Decrypt Operations |
|----------|---------------------------|-------------------|
| One-tier (current) | 50 x full content blocks | 50 individual block decrypts |
| Two-tier (metadata + content) | 50 x ~4 KB metadata blocks | 50 individual block decrypts |
| Three-tier with packed pages | 1 x ~10 KB listing page | **1 block decrypt** |

The three-tier model with packed listing pages reduces both I/O and decryption by 50x for the most common operation.

### BTree Integration

The primary BTree continues to index `EmailHashedID -> BlockLocation`, but the leaf entry expands to store pointers to all tiers:

```
Current LeafEntry (48 bytes):  Key(32) + BlockId(16) = 48 bytes
                               (offsets derived at runtime from block index)
Proposed LeafEntry (64 bytes): Key(32) + MetadataBlockId(16) + ContentBlockId(16)
```

This reduces MaxEntries from 82 to ~61 per leaf -- still well above the underflow threshold. Tree height remains 4 for 10M entries. The listing tier is accessed through the folder page system, not through the BTree.

---

## 5. Folder Listing: Paginated Pages + Delta WAL

This is the core recommendation. Inspired by Dovecot's `index + transaction log` pattern, adapted for EmailDB's append-only block storage.

### Architecture

Each folder has three structures:

```
FolderPageDirectory (1 block per folder)
  |
  +-- FolderPage 0 (newest 100 emails, sorted by date desc)
  +-- FolderPage 1 (next 100 emails)
  +-- FolderPage 2
  +-- ... (N pages, ~100 listing records each)
  |
  +-- FolderDeltaLog (append-only log of changes since last page rebuild)
```

### Read Path

**Listing page N of a folder (the common case):**

1. Read `FolderPageDirectory` -- 1 block read (~2 KB)
2. Read `FolderPage[N]` -- 1 block read (~20 KB, contains ~100 listing records)
3. Read `FolderDeltaLog` -- 1 block read (small, usually < 1 KB)
4. Merge delta entries into the page in memory (fast -- delta is small)

**Total: 2-3 block reads, ~25 KB of data, 1 decrypt per block.**

Compare to current: 200,000+ reads, gigabytes of data.

### Write Path

**Adding an email to a folder:**

1. Write the EmailContent block (Tier 3) -- append
2. Write the EmailMetadata block (Tier 2) -- append
3. BTree insert: `EmailHashedID -> (MetadataBlockId, ContentBlockId)` -- append (COW path rewrite)
4. Append to `FolderDeltaLog`: `{Operation=Add, EmailHashedID, ListingRecord}` -- **~232 bytes**
5. Done. No page rewrite needed.

**Write amplification: ~232 bytes per email for the folder listing system.** Compare to rewriting a 10 MB summary block.

**Flag change (mark as read):**

1. Append to `FolderDeltaLog`: `{Operation=FlagChange, EmailHashedID, NewFlags}` -- ~40 bytes
2. Done.

### Delta Rebuild

When the delta log exceeds a threshold (e.g., 500 entries) or during compaction:

1. Read all pages + delta log
2. Apply deltas, re-sort, re-paginate
3. Write new page blocks (append, COW)
4. Write new directory pointing to new pages (incrementing `FolderVersion`)
5. Old pages become outdated (reclaimed at compaction)

**Note:** Delta logs are local-only structures. They exist solely for folder page maintenance, not for sync or replication. When compiled into pages, delta entries are consumed and discarded -- there is no retention of delta history for replication purposes. The `FolderVersion` counter on `FolderPageDirectory` is the sync mechanism: replicas compare their `FolderVersion` against the source to determine if they need updated folder pages.

### Page Structure

Each `FolderPage` block targets **~16 KB payload**:
- At ~200 bytes per listing record, each page holds ~80 entries
- 50,000 emails = ~625 pages
- Each page is individually encrypted and checksummed
- Reading page 5 requires decrypting only page 5

The `FolderPageDirectory` enables binary search by date range:

```
PageEntry:
  PageNumber:    int
  FirstDateTicks: long    (enables "jump to date" binary search)
  LastDateTicks:  long
  PageBlockId:   Ulid (16 bytes)
  RecordCount:   int
```

### Sort Orders

**Primary sort (date descending):** Served directly from pre-sorted pages. Zero additional cost.

**Alternative sorts (by subject, by sender):** For folders under 10,000 emails (~2 MB of listing data), load all pages + delta, sort in memory, return the requested page. This is fast enough for an explicit user action. For larger folders, consider lazily-materialized secondary page sets on demand.

### Performance Characteristics

| Operation | I/O | Latency (NVMe) |
|-----------|-----|-----------------|
| First page of 50K-email folder | 3 reads, ~25 KB | < 1ms |
| Arbitrary page (by number) | 3 reads, ~25 KB | < 1ms |
| Jump to date | 3 reads (dir + binary search + page) | < 1ms |
| Scroll to next page | 1 read (next page, dir cached) | < 0.5ms |
| Add email to folder | 1 append (~232 bytes) | < 0.1ms |
| Mark as read | 1 append (~40 bytes) | < 0.1ms |
| Full folder re-sort | Load all pages (~10 MB for 50K) | ~50-100ms |

---

## 6. Search Strategy

### What Fields Matter Most (by user value)

1. **Date** -- every folder view is sorted by date
2. **Folder membership** -- every operation scopes to a folder
3. **Sender (From)** -- the most common explicit search
4. **Subject** -- users search subjects constantly
5. **Read/Unread/Flagged** -- filtering, not really "search"
6. **Recipients (To/Cc)** -- important for "Sent" folder
7. **Body text** -- full-text search, hardest and least common
8. **Has attachment** -- binary filter
9. **Attachment filenames** -- rare but high-value

### Phase 1: Secondary BTree Indexes (Immediate Value)

Reuse the existing `BTreeIndex` infrastructure to create additional trees keyed on searchable fields:

| Index | Key | Value | Use Case |
|-------|-----|-------|----------|
| **DateIndex** | date ticks (8 bytes) | EmailHashedID | "Emails from last week" |
| **SenderIndex** | BLAKE3(normalized_sender) | EmailHashedID | "Emails from alice@" |
| **FolderDateIndex** | BLAKE3(folder + date) | EmailHashedID | "Inbox sorted by date" (alternative to pages) |

**Performance at 10M emails:**
- Exact sender lookup: O(log N) = 3-4 node reads
- Date range query: O(log N + K) where K = results
- Combined queries: intersect results from two trees

**Index size:** ~460 MB per secondary BTree for 10M emails.

**Write cost:** One insert per secondary BTree per email add = 12-16 blocks total (COW path rewrite per tree).

Each secondary index gets its own `IndexRoot` block, tracked in `MetadataContent`.

### Phase 2: Listing Page Subject/Sender Scan

The paginated listing pages already contain subject and sender strings. For searches like "emails about invoice", scan the listing pages' subject fields sequentially. At 10M emails across ~125,000 pages:

- Sequential scan of all pages: ~2 GB of data, 1-2 seconds on NVMe
- Folder-scoped scan (50K emails): ~10 MB, ~10ms

This is the "80% solution" for subject/sender text search without any additional index.

### Phase 3: Segment-Based Inverted Indexes (Full-Text Body Search)

For searching email body text, implement a simplified Tantivy/Lucene-style architecture where each "segment" is stored as EmailDB blocks:

| Block Type | Purpose |
|------------|---------|
| FTSSegmentMeta | Segment metadata (field list, doc count) |
| FTSTermDictionary | FST-encoded term -> posting list offset map |
| FTSPostingList | Compressed posting lists |
| FTSSearchRoot | Root block pointing to all active segments |

Each segment is **immutable** -- a perfect fit for append-only storage. New emails are batched into new segments. Searching merges across segments. Compaction merges segments.

**Performance at 10M emails:** ~15-30 block reads for a single-term query. With caching: 2-5 reads after warm-up.

**Index size:** 15-25% of raw text size. For 10M emails (~50 GB text): ~7.5-12.5 GB.

### Phase 4: Bloom Filters (Refinement Layer)

Per-folder or per-segment bloom filters for quick elimination before reading content blocks.

- 1% false positive rate needs ~10 bits per element
- 10M emails: ~12 MB total
- Nearly free, reduces I/O for any query that touches content blocks

### Phase 5: Vector Embeddings (Semantic Search)

Store embeddings (768-1536 floats per email) in their own block type. HNSW or IVF index as additional blocks. This complements traditional search:

| Traditional search wins | Vector search wins |
|------------------------|-------------------|
| "from:alice@example.com" | "emails about the project delay" |
| "invoice #12345" | "that thing Bob sent about the conference" |
| "has:attachment AND from:bob" | Cross-language matching |
| Date range queries | Concept grouping |

Plan the block type reservations now, implement later.

---

## 7. Encryption Considerations

### Current Encryption Architecture

- Default policy encrypts: EmailContent, Folder, FolderTree, Segment, WAL
- Default policy leaves unencrypted: Metadata, Cleanup, BTreeLeaf, BTreeInternal, IndexRoot
- Per-block AES-GCM with key epoch rotation via a dedicated 2-byte `KeyEpoch` field in the block header (range 0-65535)
- Flags byte has defined bits: bit 0 = encrypted, bit 1 = compressed, bit 2 = tombstone, bit 3 = checkpoint, bits 4-7 reserved
- 28 bytes overhead per encrypted block (12-byte nonce + 16-byte auth tag)

### Recommended Policy for New Block Types

| Block Type | Encrypt? | Rationale |
|------------|----------|-----------|
| FolderPageDirectory | Yes | Contains folder structure info |
| FolderPage | Yes | Contains subject lines, sender addresses |
| FolderDeltaLog | Yes | Contains listing record data |
| EmailMetadata (Tier 2) | Yes | Contains full headers |
| Secondary BTree nodes | No | Keys are BLAKE3 hashes (opaque). Follows existing BTree policy |
| FTS blocks | Yes | Term dictionaries expose search terms |
| BloomFilter | No | Opaque bit arrays, minimal leakage |

### Practical Impact

With paginated listing pages, each page block (~16 KB) is encrypted independently. Decrypting a 16 KB block with AES-GCM on modern hardware with AES-NI takes ~1-2 microseconds. Reading a folder page requires decrypting 2-3 blocks -- negligible overhead.

Contrast with encrypting a 10 MB monolithic summary block: ~10 milliseconds of decrypt time, plus the entire block must be in memory at once.

---

## 8. Compaction & Maintenance

### The Multi-Frequency Update Problem

Different block types have vastly different mutation frequencies:

| Block Type | Mutation Frequency | Compaction Priority |
|------------|-------------------|-------------------|
| Email content (Tier 3) | Written once, never updated | Low |
| Email metadata (Tier 2) | Written once, rarely updated | Low |
| Listing pages | Updated on flag changes, new arrivals | Medium |
| Folder delta logs | Appended constantly | Cleared on page rebuild |
| BTree nodes | Rewritten on every insert (COW) | High (most dead blocks) |

### Recommended: Tiered Compaction

**Level 1 -- BTree Node Compaction (frequent, lightweight)**
- Trigger: Dead BTree blocks exceed 2x live node count
- Action: Rewrite only BTree nodes + IndexRoot
- At 10M emails: live BTree is ~146K nodes x ~4 KB = ~584 MB. Takes seconds.

**Level 2 -- Listing Page Rebuild (periodic)**
- Trigger: Delta log exceeds threshold, or part of compaction
- Action: Per-folder, apply deltas to pages, re-sort, re-paginate
- Clears delta logs, consolidates fragmented pages

**Level 3 -- Full Compaction (rare)**
- Trigger: Manual or file size exceeds 2x live data
- Action: Full rewrite of all live blocks
- Restores optimal disk layout: BTree nodes together, listing pages grouped by folder, content blocks sequential

### Compaction and Encryption

During compaction with `reEncrypt = true`, only blocks being rewritten get re-encrypted with the active DEK. Content blocks skipped by tiered compaction retain their original key epoch. The dedicated `KeyEpoch` field in the block header (2 bytes, separate from Flags) ensures correct DEK lookup at read time.

---

## 9. Cache Architecture

### Tier-Aware Caching

Extend `CacheManager` with tier-specific pools:

**Pinned (never evicted):**
- BTree top 2-3 levels: 1 + 54 + 2,916 = ~2,971 nodes x 4 KB = **~12 MB**
- Active folder page directories: ~2 KB per folder x 100 hot folders = **~200 KB**

**Hot LRU pool (large, evict oldest):**
- Folder listing pages: 16 KB each. Cache 1,000 pages (covering ~80K emails across top folders) = **~16 MB**
- Folder delta logs: usually small, always cached after first read

**Warm LRU pool (medium, evict aggressively):**
- Email metadata blocks (Tier 2): ~4 KB each. Cache last 500 opened emails = **~2 MB**

**Cold (no caching):**
- Email content blocks (Tier 3): too large, read on demand, evict immediately

**Total cache budget: ~30 MB** for excellent performance across all access patterns.

### Prefetch Strategy

- **Folder open:** Prefetch next 2-3 listing pages beyond the visible screen
- **Email open:** Prefetch next 2-3 emails' metadata blocks in the listing (users read sequentially)
- **BTree traversal:** Prefetch sibling leaf nodes during range queries

---

## 10. Approaches Considered & Rejected

### Folder Summary Blocks (Single Block Per Folder)

A single block containing all listing records for a folder.

**Rejected because:** Adding one email to a 50K-email folder requires rewriting ~10 MB. At 100 emails/day, that is 1 GB/day of write amplification for a single folder. Also, the entire block must be decrypted as one unit -- no partial reads.

### Columnar/Packed Metadata Index

Store listing fields in separate column blocks per folder (one for dates, one for subjects, etc.).

**Rejected because:** Complexity is high (maintain parallel arrays with consistent ordering across column blocks), and the selectivity advantage is minimal -- folder listings always need all display columns for each visible row.

### Per-Folder Secondary BTree

A separate BTree per folder, keyed by date, with leaf nodes containing listing records.

**Rejected because:** At 304 bytes per listing record, only 13 entries fit per leaf node. A 50K-email folder needs ~3,846 leaf nodes. Scanning the folder requires 3,846 random reads -- **worse than a flat sequential scan**. BTrees are efficient when entries are small; at 304 bytes per entry, it is essentially a linked list of small pages.

### Materialized View Blocks

Periodically rebuilt complete folder views with a delta log between rebuilds.

**Rejected as standalone because:** Essentially a more complex version of paginated pages. The delta log idea was adopted into the recommended approach, but the monolithic view block has the same encryption and partial-read problems as folder summary blocks.

### Trigram Index (for Search)

Trigram indexes for substring matching across email fields.

**Rejected because:** Index size is 3-10x the original data. For 50 GB of email text: 150-500 GB. Prohibitive at scale. Write amplification is also extreme (hundreds of trigrams per email).

---

## 11. New Block Types & Models

### Block Type Enum Additions

```
Current:
  Metadata = 0, WAL = 1, FolderTree = 2, Folder = 3,
  Segment = 4, Cleanup = 5, BTreeLeaf = 6, BTreeInternal = 7,
  IndexRoot = 8, EmailContent = 9, KeyStore = 10

Proposed additions:
  Checkpoint = 11             // Checkpoint marker block (new in v2)
  EmailMetadata = 12          // Tier 2: full email headers/envelope
  FolderPageDirectory = 13    // Page index per folder
  FolderPage = 14             // One page of listing records
  FolderDeltaLog = 15         // Append-only change log per folder

  // Phase 2+ (reserve now, implement later)
  FTSSegmentMeta = 16         // Full-text search segment metadata
  FTSTermDictionary = 17      // FST-encoded term dictionary
  FTSPostingList = 18         // Compressed posting lists
  FTSSearchRoot = 19          // Root of FTS index

  // Phase 3+ (reserve now)
  BloomFilter = 20             // Block/segment-level bloom filters

  // Phase 4+ (reserve now)
  EmbeddingContent = 21        // Vector embeddings per email
  VectorIndexNode = 22         // HNSW/IVF index nodes
  VectorIndexRoot = 23         // Vector index root
```

### Key Models

**FolderListingRecord** (~200 bytes, used inside FolderPage blocks):
```
EmailHashedID:    32 bytes   // Unique key
DateUtcTicks:     8 bytes    // Sort key, display date
Flags:            4 bytes    // Read/unread, flagged, replied, forwarded, attachment, draft
MessageSize:      4 bytes    // Display size
SubjectLength:    1 byte
Subject:          up to 127 bytes (truncated/padded)
FromLength:       1 byte
From:             up to 127 bytes
ToLength:         1 byte
To:               up to 127 bytes (for Sent folder)
```

**FolderPageDirectory**:
```
FolderId:         Ulid (16 bytes)
TotalEmails:      int
PageSize:         int (target ~80 per page)
PageCount:        int
PrimarySortKey:   byte (0 = date_desc)
FolderVersion:    ulong      // Increments on every delta compile into pages/chain blocks
DeltaLogBlockId:  Ulid (16 bytes, zero ULID if no pending deltas)
Pages[]:
  PageNumber:     int
  FirstDateTicks: long       // For binary search by date
  LastDateTicks:  long
  PageBlockId:    Ulid (16 bytes)
  RecordCount:    int
```

`FolderVersion` increments every time the delta log is compiled into folder pages or chain blocks. Backup replicas compare `FolderVersion` to determine if they need updated folder pages.

**FolderDeltaLogEntry**:
```
Operation:        byte (1=Add, 2=Delete, 3=FlagChange)
EmailHashedID:    32 bytes
ListingRecord:    (present for Add/FlagChange, ~200 bytes)
```

**EmailMetadataContent** (Tier 2, individual block per email):
```
EmailHashedID:    32 bytes
Subject:          string (full, not truncated)
From:             string
To:               string
Cc:               string
Bcc:              string
Date:             DateTime
MessageId:        string
InReplyTo:        string
References:       string
AttachmentCount:  int
ContentSize:      long
Flags:            byte
ContentBlockId:   Ulid (16 bytes) // Pointer to Tier 3 block (offset derived at runtime)
FolderPath:       string
Snippet:          string (~200 chars body preview)
MimeStructure:    byte[] (serialized MIME tree without content)
```

### MetadataContent Extensions

```
Existing fields:
  WALBlockId, FolderTreeBlockId, SegmentBlockIds, OutdatedBlockIds
  (all Ulid references; file offsets are derived at runtime from the block index)

New fields:
  DateIndexRootBlockId:       Ulid = Ulid.Empty    // Secondary BTree root
  SenderIndexRootBlockId:     Ulid = Ulid.Empty    // Secondary BTree root
  FolderDateIndexRootBlockId: Ulid = Ulid.Empty    // Secondary BTree root
  FTSSearchRootBlockId:       Ulid = Ulid.Empty    // Full-text search root
```

---

## 12. Migration Path

The existing `FolderContent.EmailIds` list remains as the **source of truth** for folder membership. Paginated pages are a derived/cached structure. This provides clean migration:

1. **First access to a folder with no pages:** Generate them from `EmailIds` + BTree lookups (one-time cost)
2. **Going forward:** Maintain pages via delta log
3. **On corruption/loss of pages:** Regenerate from `EmailIds` (same as step 1)
4. **Existing databases:** Work immediately. Pages generated lazily on first folder open

The system degrades gracefully: if page data is lost or corrupted, it is regenerated from the authoritative `EmailIds` list + BTree. This is the same resilience model Dovecot uses (the index can always be rebuilt from the mail store).

---

## 13. Phased Implementation Plan

### Phase 1: Three-Tier Data Model + Paginated Folder Pages

**Scope:**
- Add `BlockType.EmailMetadata`, `BlockType.FolderPageDirectory`, `BlockType.FolderPage`, `BlockType.FolderDeltaLog`
- Create `FolderListingRecord`, `FolderPageDirectory`, `EmailMetadataContent` models
- Split `EnhancedEmailContent` into three-tier models
- Implement page generation, delta logging, and merge logic in `FolderManager`
- Expand `LeafEntry` from 48 to 64 bytes (Key(32) + MetadataBlockId(16) + ContentBlockId(16))
- Update `CacheManager` with tier-aware pools
- Update encryption policy for new block types
- Update `BTreeNodeSerializer` for expanded leaf entries

**Delivers:** Sub-millisecond folder listing at any scale. The single highest-impact change.

### Phase 2: Secondary BTree Indexes

**Scope:**
- Implement DateIndex, SenderIndex, FolderDateIndex as additional BTree instances
- Track secondary index roots in `MetadataContent`
- Update email write path to insert into secondary indexes

**Delivers:** Structured search (by sender, by date range, within folder).

### Phase 3: Full-Text Search

**Scope:**
- Implement segment-based inverted index (or wrap Lucene.NET with custom Directory)
- Add FTS block types
- Implement segment merging during compaction

**Delivers:** Body text search across all emails.

### Phase 4: Bloom Filters + Performance Refinement

**Scope:**
- Per-segment bloom filters for elimination before content reads
- Tiered compaction implementation
- Prefetch strategy in CacheManager

**Delivers:** Reduced I/O for search, better compaction performance.

### Phase 5: Vector Embeddings

**Scope:**
- Embedding storage block type
- HNSW/IVF index in blocks
- Hybrid query pipeline (structured filters -> text search -> vector re-rank)

**Delivers:** Semantic search ("emails about the project delay").

---

## Appendix: Performance Comparison Summary

| Operation | Current Architecture | Proposed Architecture |
|-----------|---------------------|----------------------|
| List folder (50K emails, first page) | ~200,000 random reads, 2-50s | **3 reads, < 1ms** |
| List folder (arbitrary page) | Same | **3 reads, < 1ms** |
| Add email to folder | Rewrite FolderContent (~1.6 MB) | **Append ~232 bytes** |
| Mark email as read | Rewrite FolderContent | **Append ~40 bytes** |
| Open specific email | 4 BTree reads + 1 content read | 4 BTree reads + 1 metadata + 1 content read |
| Search by sender | Scan all emails | **O(log N) BTree lookup** |
| Search by date range | Scan all emails | **O(log N + K) range query** |
| Search subject text | Scan all emails | **Scan listing pages (~10ms per 50K emails)** |
| Full-text body search | Impossible without loading all content | **Inverted index, ~10ms** (Phase 3) |
| Memory for folder listing | 10M x full email size | **~30 MB cache total** |

---

## References

- Dovecot Mail Index Format: https://doc.dovecot.org/2.3/developer_manual/design/indexes/index_file_format/
- Dovecot Index Cache: https://doc.dovecot.org/2.3/developer_manual/design/indexes/index_file_format_cache/
- Cyrus IMAP Mailbox Formats: https://www.cyrusimap.org/imap/developer/guidance/mailbox-format.html
- Thunderbird Pluggable Mail Stores: https://wiki.mozilla.org/Thunderbird:Pluggable_Mail_Stores
- Apple Mail Database Schema: https://labs.wordtothewise.com/mailapp/
- RocksDB Block-Based Table Format: https://github.com/facebook/rocksdb/wiki/Rocksdb-BlockBasedTable-Format
- LevelDB Table Format: https://github.com/google/leveldb/blob/main/doc/table_format.md
- CouchDB B-Tree Architecture: https://guide.couchdb.org/draft/btree.html
- Tantivy Architecture: https://github.com/quickwit-oss/tantivy/blob/main/ARCHITECTURE.md
- FastMail Storage Architecture: https://www.fastmail.help/hc/en-us/articles/1500000278242
- SQLite B-Tree Internals: https://fly.io/blog/sqlite-internals-btree/
- WiredTiger Cache Architecture: https://source.wiredtiger.com/develop/arch-cache.html
