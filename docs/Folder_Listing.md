# Folder Listing — Three-Tier Model (v3)

How folder browsing stays fast at 50K+ emails per folder. Block layouts are normative in the [File Format Spec](../EmailDB_FileFormat_Spec.md); block types here use v3 numbering.

## 1. The three tiers

Email data is split by access temperature so common operations touch minimal I/O:

| Tier | BlockType | Contents | ~Size | Read when |
|------|-----------|----------|-------|-----------|
| 1 | FolderPage (12) | Packed listing records | ~400 B/email | Browsing a folder |
| 2 | EmailMetadata (10) | Full RFC 5322 headers, MIME structure, threading refs, **Preview** | ~4 KB/email | Opening an email |
| 3 | EmailContent (7) | Raw MIME body, inline images, attachments | variable | Viewing body / attachments |

Tier 2's `Preview` field (~200 chars of plain-text body) exists so every Tier 1 field can be regenerated from Tier 2 alone — recovery never reads Tier 3.

## 2. Per-folder structures

```
FolderPageDirectory (BlockType 11, one per folder)
  ├── FolderPage 0   (newest ~80 emails, date-descending)
  ├── FolderPage 1
  ├── FolderPage N
  └── HeadDeltaBlockId ──▶ FolderDeltaLog chain (BlockType 13)
```

### FolderPageDirectory (11)

One block per folder; rewritten (appended as a new version, same BlockId) when pages or the delta head change.

| Field | Description |
|-------|-------------|
| FolderId | ULID of the folder |
| FolderVersion | Monotonic counter; increments on every directory rewrite — drives sync replication |
| HeadDeltaBlockId | Newest FolderDeltaLog block (0 = none pending) |
| PageEntries[] | `{ PageBlockId (Ulid), DateFrom, DateTo, EntryCount }` — date ranges enable binary search by date |

### FolderPage (12)

Packed Tier 1 listing records, sorted date-descending, ~80 records per page (~35 KB block). Record fields: `EmailHashedID (32)`, `EmailContentBlockId (16)`, `DateTicks (8)`, `Flags (4: read/flagged/answered/draft)`, `MessageSize (8)`, then length-prefixed `From`, `Subject`, `Preview`. Encrypted under the Default policy (subjects and senders are sensitive).

### FolderDeltaLog (13)

Append-only chain of small blocks buffering folder mutations between page rebuilds. Each block: `PreviousDeltaBlockId` + entries `{ Op (Add/Delete/FlagChange), EmailHashedID, listing record for Add, Flags for FlagChange }`. An Add entry is ~450 B on disk.

There is no in-place rewriting anywhere (the v2 "dedicated region" is gone): a new delta block is appended and the directory's `HeadDeltaBlockId` advances.

## 3. Operations

**List a page** (e.g. newest 50 of a 50K-email folder):
1. Read FolderPageDirectory (1 block, usually cached)
2. Read newest FolderPage (1 block, 1 decrypt)
3. Read pending delta chain (usually 0–1 blocks) and merge in memory

Cost: 2–3 block reads, ~35–70 KB, 1–2 decrypts. Jumping to a date = binary search over `PageEntries` date ranges, then read that page.

**Add an email:** append a delta block (or extend at next batch), rewrite the directory (FolderVersion + 1). No page rewritten.

**Compile deltas → pages** (threshold ~500 pending entries, or on close):
1. Merge delta chain into affected pages' record sets
2. Write new FolderPage blocks (COW; ~7 pages rewritten for 500 adds)
3. Write new directory: updated PageEntries, `HeadDeltaBlockId = 0`, FolderVersion + 1
4. Old pages, old directory version, and the delta chain become dead

**Move/delete** are delta entries in both folders' logs; email content blocks are untouched (folder membership lives only in Tier 1 structures — FolderContent-style per-email lists do not exist in v3).

## 4. Regeneration (recovery path)

If a folder's pages are lost or corrupt, rebuild from Tier 2:
1. Enumerate the folder's email IDs (from FolderTree/delta history or a full EmailMetadata sweep)
2. For each: read EmailMetadata → extract Subject/From/Date/Size from headers, Preview from the Preview field; Flags reset to defaults
3. Sort by date, repack into pages, write a fresh directory

Cost: one Tier 2 read per email (50K reads for a 50K folder) — a rare recovery operation, not a normal path.

## 5. Sync interaction

`FolderVersion` is the replication unit: the backup compares directory versions and pulls changed folders' directory + pages + delta chain wholesale. See [Sync](Sync.md).
