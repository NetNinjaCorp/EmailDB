# Folder Listing: Three-Tier Email Model

## The Problem

Rendering a folder listing requires display metadata (subject, sender, date, flags) for each email. If this data lives inside the full EmailContent block, listing a 50,000-email folder requires 200,000+ random reads and decrypting megabytes of content per email just to show a subject line.

## Three-Tier Storage

Email data is split by access temperature:

### Tier 1 -- Listing Record (~200 bytes)

What you need to render one row in a folder view. Stored packed inside folder pages.

| Field | Size | Purpose |
|-------|------|---------|
| EmailHashedID | 32 bytes | Unique key |
| DateUtcTicks | 8 bytes | Sort key, display date |
| Flags | 4 bytes | Read/unread, flagged, replied, forwarded, has-attachment, draft |
| MessageSize | 4 bytes | Display size |
| Subject | ~128 bytes | Truncated for display |
| From | ~128 bytes | Sender for display |
| To | ~128 bytes | First recipient (for Sent folder) |

### Tier 2 -- EmailMetadata (~4 KB per email, BlockType = 12)

Full headers and envelope. Read when opening a specific email.

- All RFC 5322 headers (To, Cc, Bcc, Reply-To, X-headers)
- MIME structure tree (part types, sizes, filenames) without content
- Message-ID, In-Reply-To, References (for threading)
- Content preview / snippet (~200 chars)
- ContentBlockId -- pointer to Tier 3

### Tier 3 -- EmailContent (BlockType = 9)

Raw MIME body, inline images, attachments. Read only when the user wants to see the body.

### Why Three Tiers, Not Two

| Approach | Data per page (50 emails) | Decrypt operations |
|----------|---------------------------|-------------------|
| One-tier (full content) | 50 x full content blocks | 50 decrypts |
| Two-tier (metadata + content) | 50 x ~4 KB blocks | 50 decrypts |
| **Three-tier with packed pages** | **1 x ~10 KB page** | **1 decrypt** |

## Paginated Folder Pages

Each folder has three structures:

```
FolderPageDirectory (1 block per folder, BlockType = 13)
  |
  +-- FolderPage 0 (newest ~80 emails, sorted by date desc)
  +-- FolderPage 1
  +-- FolderPage N
  |
  +-- FolderDeltaLog (append-only change log, BlockType = 14)
```

### FolderPageDirectory

| Field | Type | Description |
|-------|------|-------------|
| FolderId | Ulid | Folder identifier |
| FolderVersion | ulong | Incremented on every delta compile into pages |
| TotalEmails | int | Total emails in folder |
| PageSize | int | Target entries per page (~80) |
| PageCount | int | Number of pages |
| PrimarySortKey | byte | Sort order (0 = date descending) |
| DeltaLogBlockId | Ulid | Current delta log block |
| Pages[] | array | Page index entries |

Each page index entry:

| Field | Type | Description |
|-------|------|-------------|
| PageNumber | int | Page number |
| FirstDateTicks | long | Earliest date in page (enables binary search) |
| LastDateTicks | long | Latest date in page |
| PageBlockId | Ulid | Block containing this page |
| RecordCount | int | Entries in this page |

### FolderDeltaLog

Append-only log of changes since last page rebuild:

| Field | Type | Description |
|-------|------|-------------|
| Operation | byte | 1=Add, 2=Delete, 3=FlagChange |
| EmailHashedID | 32 bytes | Target email |
| ListingRecord | ~200 bytes | Present for Add/FlagChange |

The delta log is **local-only** -- it exists solely for folder page maintenance, not for sync. When compiled into pages, delta entries are consumed and discarded. The `FolderVersion` counter drives sync (see [Sync](Sync.md)).

## Read Path

**Listing page N of a folder:**

1. Read `FolderPageDirectory` -- 1 block (~2 KB)
2. Read `FolderPage[N]` -- 1 block (~16 KB, ~80 listing records)
3. Read `FolderDeltaLog` -- 1 block (small, usually < 1 KB)
4. Merge delta entries into the page in memory

**Total: 2-3 block reads, ~25 KB, 1 decrypt per block.**

## Write Path

**Adding an email:**

1. Append EmailContent block (Tier 3)
2. Append EmailMetadata block (Tier 2)
3. BTree insert: `EmailHashedID -> BlockId`
4. Append to FolderDeltaLog: `{Add, EmailHashedID, ListingRecord}` (~232 bytes)

**No page rewrite.** Write amplification for the folder system: ~232 bytes per email.

**Flag change (mark as read):** Append to FolderDeltaLog: `{FlagChange, EmailHashedID, NewFlags}` (~40 bytes).

## Delta Rebuild

When the delta log exceeds a threshold (~500 entries) or during compaction:

1. Read all pages + delta log
2. Apply deltas, re-sort, re-paginate
3. Write new page blocks (append)
4. Write new directory incrementing `FolderVersion`
5. Old pages become dead blocks (reclaimed at compaction)

## Performance

| Operation | I/O | Latency (NVMe) |
|-----------|-----|-----------------|
| First page of 50K-email folder | 3 reads, ~25 KB | < 1ms |
| Arbitrary page by number | 3 reads, ~25 KB | < 1ms |
| Jump to date | 3 reads (dir + binary search + page) | < 1ms |
| Add email to folder | 1 append (~232 bytes) | < 0.1ms |
| Mark as read | 1 append (~40 bytes) | < 0.1ms |

## BTree Integration

The primary BTree maps `EmailHashedID -> BlockId` (48 bytes per entry). The listing tier is accessed through the folder page system, not through the BTree. The BTree is only needed for point lookups of specific emails by their hashed ID.

## Migration

`FolderContent.EmailIds` remains the source of truth for folder membership. Paginated pages are a derived structure. On first access to a folder with no pages, they are generated from `EmailIds` + BTree lookups. If pages are lost or corrupted, they are regenerated from the authoritative `EmailIds` list.
