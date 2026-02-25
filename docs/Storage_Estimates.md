# Storage Estimates

Reference tables for capacity planning. All numbers assume v2 format (91 bytes fixed overhead per block).

## Constants

| Component | Size |
|-----------|------|
| Block fixed overhead | 91 bytes |
| BTree leaf entry | 48 bytes (32B key + 16B BlockId) |
| BTree leaf max entries | 82 |
| BTree internal max keys / children | 54 / 55 |
| BTree node payload budget | 4,036 bytes |
| EmailHashedID | 32 bytes (SHA3-256) |
| Encryption overhead per block | 28 bytes (12B nonce + 16B auth tag) |

## Email Content Storage

Each email = 1 block: `email_size + 91 bytes`

| Emails | 1 KB avg | 5 KB avg | 75 KB avg | 500 KB avg | 2 MB avg |
|--------|----------|----------|-----------|------------|----------|
| 10K | 10.6 MB | 49.7 MB | 733 MB | 4.77 GB | 19.5 GB |
| 100K | 106 MB | 497 MB | 7.33 GB | 47.7 GB | 195 GB |
| 1M | 1.06 GB | 4.97 GB | 73.3 GB | 477 GB | 1.95 TB |
| 10M | 10.6 GB | 49.7 GB | 733 GB | 4.77 TB | 19.5 TB |

## BTree Primary Index

| Emails | Leaf Nodes | Height | Total Index Size |
|--------|-----------|--------|------------------|
| 10K | 176 | 3 | ~511 KB |
| 100K | 1,755 | 4 | ~4.9 MB |
| 1M | 17,544 | 4 | ~49 MB |
| 10M | 175,439 | 5 | ~494 MB |
| 100M | 1,754,386 | 5 | ~4.82 GB |

## Folder Storage

Assuming realistic folder distribution:

| Emails | Folders | Avg Emails/Folder | Total Folder Data |
|--------|---------|-------------------|-------------------|
| 10K | 10 | 1,000 | ~313 KB |
| 100K | 50 | 2,000 | ~3.1 MB |
| 1M | 200 | 5,000 | ~30.5 MB |
| 10M | 500 | 20,000 | ~305 MB |

## System Blocks (always negligible)

| Block | Typical Size |
|-------|-------------|
| Header | ~140 bytes |
| Metadata | ~200 bytes |
| FolderTree | 200 bytes - 5 KB |
| WAL (flushed) | ~100 bytes |
| KeyStore | ~200 bytes |
| Checkpoint | ~220 bytes |

## Total Overhead (non-content)

| Emails | BTree | Folders | System | Total Overhead |
|--------|-------|---------|--------|----------------|
| 10K | 511 KB | 313 KB | ~1 KB | ~825 KB |
| 100K | 4.9 MB | 3.1 MB | ~2 KB | ~8 MB |
| 1M | 49 MB | 30.5 MB | ~5 KB | ~80 MB |
| 10M | 494 MB | 305 MB | ~10 KB | ~800 MB |

## Three-Tier Additions (when implemented)

| Component | Per Email | 10K | 1M | 10M |
|-----------|----------|-----|-----|-----|
| Tier 1: Listing pages | ~265 bytes | 2.6 MB | 265 MB | 2.65 GB |
| Tier 2: Email metadata | ~5 KB | 50 MB | 5 GB | 50 GB |
| Secondary BTrees (x3) | varies | 1.5 MB | 150 MB | 1.5 GB |

## Storage Distribution

At typical email sizes (75 KB avg), email content is 99.9%+ of total file size. The BTree index is ~48 bytes per email in live leaves. The 91-byte block overhead is 0.1% for typical emails.

## Per-Email Write Cost

| Step | Bytes | Pattern |
|------|-------|---------|
| EmailContent block | email_size + 91 | Sequential append |
| WAL entry | 48 | Sequential write |
| Folder delta log | ~232 | Sequential append |
| **Total** | **email_size + ~371** | **All sequential** |

No BTree traversal on the write path. BTree updated in background batches (~82 emails per flush).
