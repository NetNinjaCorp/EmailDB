# EmailDB Storage Estimation & Block Distribution

## Constants

| Component | Size |
|-----------|------|
| Block fixed overhead (header + checksums + footer) | **91 bytes** |
| BTree leaf entry (`LeafEntry`) | **48 bytes** (32B key + 16B BlockId) |
| BTree leaf max entries | **82** |
| BTree internal max keys / children | **54 / 55** |
| BTree node payload budget | **4,036 bytes** |
| Full leaf block | **~4,073 bytes** |
| Full internal block | **~4,065 bytes** |
| IndexRoot block | **158 bytes** |
| EmailHashedID (key) | **32 bytes** (SHA3-256) |

Assuming **~70% fill factor** (ln(2) for random hash keys), average leaf holds ~57 entries (~2,873 bytes), average internal holds ~38 keys (~2,913 bytes).

---

## 1. Email Content Storage (dominant cost)

Each email = 1 block: `email_size + 91 bytes overhead`

| Emails | **1 KB** (tiny text) | **5 KB** (text) | **75 KB** (typical) | **500 KB** (moderate) | **2 MB** (large) | **25 MB** (max) |
|--------|---------------------|-----------------|--------------------|-----------------------|------------------|-----------------|
| **10K** | 10.6 MB | 49.7 MB | 733 MB | 4.77 GB | 19.5 GB | 244 GB |
| **100K** | 106 MB | 497 MB | 7.33 GB | 47.7 GB | 195 GB | 2.44 TB |
| **1M** | 1.06 GB | 4.97 GB | 73.3 GB | 477 GB | 1.95 TB | 24.4 TB |
| **10M** | 10.6 GB | 49.7 GB | 733 GB | 4.77 TB | 19.5 TB | 244 TB |
| **100M** | 106 GB | 497 GB | 7.33 TB | 47.7 TB | 195 TB | 2.44 PB |

---

## 2. BTree Primary Index (independent of email size)

| Emails | Leaves | L1 Internal | L2 | L3 | L4 | Height | **Total Index** |
|--------|--------|-------------|-----|-----|-----|--------|-----------------|
| **10K** | 176 | 5 | 1 | - | - | 3 | **~511 KB** |
| **100K** | 1,755 | 47 | 2 | - | - | 4 | **~4.9 MB** |
| **1M** | 17,544 | 462 | 13 | 1 | - | 4 | **~49 MB** |
| **10M** | 175,439 | 4,617 | 122 | 4 | 1 | 5 | **~494 MB** |
| **100M** | 1,754,386 | 46,169 | 1,215 | 32 | 1 | 5 | **~4.82 GB** |

---

## 3. Folder Storage

Each `FolderContent` block stores all `EmailHashedID`s (32 bytes each) for that folder. Assuming a realistic folder distribution:

| Emails | Folders | Emails/Folder | Per Folder Block | **Total Folders** |
|--------|---------|---------------|------------------|-------------------|
| **10K** | 10 | 1,000 | ~31 KB | **~313 KB** |
| **100K** | 50 | 2,000 | ~63 KB | **~3.1 MB** |
| **1M** | 200 | 5,000 | ~156 KB | **~30.5 MB** |
| **10M** | 500 | 20,000 | ~625 KB | **~305 MB** |
| **100M** | 1,000 | 100,000 | ~3.05 MB | **~3.05 GB** |

---

## 4. System Blocks (negligible at all scales)

| Block | Typical Size | Notes |
|-------|-------------|-------|
| Header (Block 0) | ~140 B | One per file |
| Metadata | ~200 B | Pointers to other blocks |
| FolderTree | 200 B - 5 KB | Grows with folder count |
| WAL (flushed) | ~100 B | Minimal after flush |
| KeyStore | ~200 B | Per epoch entry ~50 B |
| **System Total** | **< 10 KB** | Always negligible |

---

## 5. Total Overhead (everything except email content)

| Emails | BTree Index | Folders | System | **Total Overhead** |
|--------|-------------|---------|--------|-------------------|
| **10K** | 511 KB | 313 KB | ~1 KB | **~825 KB** |
| **100K** | 4.9 MB | 3.1 MB | ~2 KB | **~8 MB** |
| **1M** | 49 MB | 30.5 MB | ~5 KB | **~80 MB** |
| **10M** | 494 MB | 305 MB | ~10 KB | **~800 MB** |
| **100M** | 4.82 GB | 3.05 GB | ~50 KB | **~7.9 GB** |

---

## 6. Storage Distribution (% of total file size)

### Best Case: 1 KB average emails (tiny text-only)

| Emails | Email Content | BTree Index | Folders | Total File |
|--------|--------------|-------------|---------|------------|
| **10K** | 10.6 MB (93.0%) | 511 KB (4.4%) | 313 KB (2.7%) | **11.4 MB** |
| **100K** | 106 MB (93.0%) | 4.9 MB (4.3%) | 3.1 MB (2.7%) | **114 MB** |
| **1M** | 1.06 GB (93.0%) | 49 MB (4.3%) | 30.5 MB (2.7%) | **1.14 GB** |
| **10M** | 10.6 GB (93.0%) | 494 MB (4.3%) | 305 MB (2.7%) | **11.4 GB** |
| **100M** | 106 GB (93.0%) | 4.82 GB (4.2%) | 3.05 GB (2.7%) | **114 GB** |

### Typical Case: 75 KB average emails

| Emails | Email Content | BTree Index | Folders | Total File |
|--------|--------------|-------------|---------|------------|
| **10K** | 733 MB (99.89%) | 511 KB (0.07%) | 313 KB (0.04%) | **734 MB** |
| **100K** | 7.33 GB (99.89%) | 4.9 MB (0.07%) | 3.1 MB (0.04%) | **7.34 GB** |
| **1M** | 73.3 GB (99.89%) | 49 MB (0.07%) | 30.5 MB (0.04%) | **73.4 GB** |
| **10M** | 733 GB (99.89%) | 494 MB (0.07%) | 305 MB (0.04%) | **734 GB** |
| **100M** | 7.33 TB (99.89%) | 4.82 GB (0.07%) | 3.05 GB (0.04%) | **7.34 TB** |

### Worst Case: 25 MB average emails (heavy attachments)

| Emails | Email Content | BTree Index | Folders | Total File |
|--------|--------------|-------------|---------|------------|
| **10K** | 244 GB (>99.99%) | 511 KB | 313 KB | **244 GB** |
| **100K** | 2.44 TB (>99.99%) | 4.9 MB | 3.1 MB | **2.44 TB** |
| **1M** | 24.4 TB (>99.99%) | 49 MB | 30.5 MB | **24.4 TB** |
| **10M** | 244 TB (>99.99%) | 494 MB | 305 MB | **244 TB** |
| **100M** | 2.44 PB (>99.99%) | 4.82 GB | 3.05 GB | **2.44 PB** |

---

## 7. Write Path & BTree Amplification

### WAL-Buffered Write Architecture

The BTree is **not updated on every email write**. The `BTreeWALManager` buffers
inserts in memory and persists them to a 48-byte-per-entry WAL region on disk
for crash safety. The BTree is only updated during a flush, which triggers when:

- The buffer reaches **82 entries** (one full leaf worth), or
- A periodic timer fires

**Per-email write cost (what actually hits disk):**

| Step | Bytes Written | I/O Pattern |
|------|--------------|-------------|
| Append EmailContent block | email_size + 91 | Sequential append |
| WAL entry (crash-safe) | 48 | Sequential write to WAL region |
| Folder delta log entry | ~232 | Sequential append |
| **Total per email** | **email_size + ~371** | **All sequential** |

No BTree navigation, no COW path rewrite, no tree traversal on the write path.

### When the BTree is actually consulted

The BTree is a **point-lookup index for email content retrieval** — it maps
`EmailHashedID -> BlockId`. It is NOT used for:

- **Folder browsing** — handled by folder listing pages / FolderContent blocks
- **Listing subjects/senders** — handled by Tier 1 listing records
- **Recent email lookups** — served from WAL buffer in memory (O(1) dictionary lookup)

The BTree is only needed when a user clicks on a specific email that has already
been flushed from the WAL buffer into the tree. At that point, the lookup is
3-5 block reads (tree height). Recent emails are always in the WAL buffer and
never touch the BTree at all.

### Flush behavior (background, every ~82 emails)

During a flush, the WAL buffer is sorted by key and each entry is inserted into
the BTree via COW path rewrites. The current implementation inserts individually
in a loop, so each entry rewrites its root-to-leaf path:

| Tree Height | Emails in Tree | Blocks Written Per Flush | Bytes Per Flush |
|-------------|---------------|--------------------------|-----------------|
| 1-2 | < 4,500 | ~82-164 | ~240-480 KB |
| 3 | < 250K | ~246 | ~720 KB |
| 4 | < 13.6M | ~328 | ~960 KB |
| 5 | < 750M | ~410 | ~1.2 MB |

This happens in the background. The write path (append + WAL entry) is unaffected.

### Cumulative dead BTree blocks during bulk import

Over a full bulk import, dead BTree nodes accumulate from COW path rewrites.
The tree height increases during import, so early inserts are cheaper than
later ones:

| Emails | Avg Height During Import | Dead BTree Before Compaction | Live BTree After Compaction | Amplification |
|--------|-------------------------|-----------------------------|-----------------------------|---------------|
| **10K** | 2.5 | ~74 MB | ~511 KB | ~149x |
| **100K** | 3.0 | ~870 MB | ~4.9 MB | ~177x |
| **1M** | 3.7 | ~11 GB | ~49 MB | ~230x |
| **10M** | 4.0 | ~117 GB | ~494 MB | ~242x |
| **100M** | 4.9 | ~1.4 TB | ~4.82 GB | ~302x |

**Important context for these numbers:**

- Dead blocks accumulate **only during the import phase**, in the background
- They do **not** block the write path (writes are append + 48-byte WAL entry)
- They are **fully reclaimed** by a single compaction pass
- For an archival system (import once, read many), you compact once after import
  and the file contains only live data from that point forward

### The archival workflow

```
Import emails  -->  Compact once  -->  Read-mostly steady state
  (dead blocks       (reclaim all       (BTree is static,
   accumulate)        dead space)         no further amplification)
```

In steady state after compaction, the BTree is essentially read-only. Occasional
new emails go through the WAL and trigger infrequent flushes. The amplification
numbers above represent a one-time cost during initial import.

### Future optimization: batch-aware flush

The current flush inserts entries individually (82 separate COW path rewrites per
flush). A batch-aware strategy could:

1. Sort entries by target leaf
2. Apply all inserts to each leaf in one pass
3. Propagate changes upward, creating each internal node only once per batch

This would reduce per-flush amplification by roughly the tree height (~3-5x),
bringing the total dead blocks during import down significantly. A full bulk-load
(bottom-up tree construction) during initial import would approach 1x amplification.

---

## 8. Proposed Three-Tier Additions (future overhead)

If/when the proposed folder listing architecture is implemented:

| Component | Per Email | 10K | 100K | 1M | 10M | 100M |
|-----------|----------|-----|------|-----|-----|------|
| **Tier 1: Listing pages** | ~265 B | 2.6 MB | 26 MB | 265 MB | 2.65 GB | 26.5 GB |
| **Tier 2: Email metadata** | ~5 KB | 50 MB | 500 MB | 5 GB | 50 GB | 500 GB |
| **Secondary BTree indexes** (x3) | - | 1.5 MB | 15 MB | 150 MB | 1.5 GB | 14.5 GB |
| **Tier subtotal** | - | 54 MB | 541 MB | 5.4 GB | 54 GB | 541 GB |

---

## Key Insights

1. **Email content is always dominant** — Even at 1 KB average, content is ~93% of storage. At typical sizes (75 KB+), content is >99.9%.

2. **Index overhead is modest** — The BTree primary index is ~48 bytes per email (in live leaf entries) plus internal node overhead. This scales linearly and tops out at ~4.82 GB for 100M emails.

3. **Writes are cheap** — The per-email write cost is just `email_size + ~371 bytes`, all sequential appends. The BTree is updated in background batches via the WAL, not on the write path.

4. **BTree amplification is a one-time import cost** — Dead blocks from COW path rewrites accumulate during bulk import but are fully reclaimed by a single compaction pass. In steady state (post-compaction, archival read-mostly), there is no ongoing amplification.

5. **Folder blocks are the second concern** — Storing all `EmailHashedID`s per folder means a 100K-email folder is a single 3.2 MB block. The proposed paginated folder pages would fix this.

6. **91 bytes per block overhead is negligible** — Even for 1 KB emails, the block framing overhead is only 8.2%. For anything larger, it rounds to zero.

7. **The proposed three-tier model adds significant overhead** — Tier 2 (email metadata at ~5 KB/email) would be a meaningful addition: 50 GB at 10M emails. Worth it for the folder listing performance gains (3 reads vs 200K reads), but it roughly doubles storage for small-email archives.
