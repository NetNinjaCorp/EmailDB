# Storage Estimates (v3)

Back-of-envelope sizing for the v3 format. Assumptions: 4096-byte index nodes, 96 B block overhead, +28 B per encrypted block, average raw email 75 KB (MIME incl. attachments amortized), Tier 2 metadata ~4 KB, Tier 1 record ~400 B.

## 1. Fixed overhead per email

| Component | Amortized cost |
|-----------|---------------|
| Block overhead (EmailContent + EmailMetadata) | 192 B |
| Encryption overhead (2 blocks + page share) | ~57 B |
| Tier 1 listing record (in shared FolderPage) | ~400 B |
| Primary index entry (48 B → leaf share) | ~49 B |
| Date index entry (24 B → leaf share) | ~25 B |
| BlockLocationIndex (~2.1 entries × 33 B leaf share) | ~70 B |
| **Total bookkeeping** | **~0.8 KB/email (~1% of a 75 KB email)** |

## 2. Node capacities (from spec Section 6.1)

3988 usable bytes per node (4096 − 96 block overhead − 12 node header):

| Index | Leaf entries | Internal fan-out |
|-------|-------------|------------------|
| PrimaryEmail (48 B entries) | 83 | 50 children |
| BlockLocation (32 B entries) | 124 | 71 children |
| Date (24 B entries) | 166 | 55 children |

Primary index capacity by height: 83 / 4,150 / 207K / **10.4M** / 519M.

## 3. Index sizes at scale

Live blocks ≈ 2.1 × emails (content + metadata + amortized pages/nodes).

| Emails | Primary index | BlockLocationIndex | Date index | Total index |
|--------|--------------|--------------------|------------|-------------|
| 10K | 0.5 MB | 0.7 MB | 0.25 MB | ~1.5 MB |
| 100K | 5 MB | 7 MB | 2.5 MB | ~15 MB |
| 1M | 50 MB | 70 MB | 25 MB | ~145 MB |
| 10M | 503 MB | 703 MB | 247 MB | ~1.5 GB |

Upper (non-leaf) levels are ~2% of each tree — a few MB even at 10M — and stay fully cached, so lookups cost ~1 uncached read.

## 4. Per-shard picture (50 GB cap)

A 50 GB shard ≈ **650K emails** at 75 KB average (more for light mailboxes: 50 GB ≈ 5M emails at 10 KB).

| Component | Size @ 650K emails | % of shard |
|-----------|--------------------|-----------|
| Tier 3 EmailContent | ~46.5 GB | 93% |
| Tier 2 EmailMetadata (~4 KB, Zstd ≈ 2×) | ~1.3 GB | 2.6% |
| Tier 1 pages + directories + deltas | ~0.3 GB | 0.6% |
| All indexes (Section 3, interpolated) | ~95 MB | 0.2% |
| Superblocks, Checkpoints, WAL, KeyStore, Metadata | < 10 MB | ~0 |
| Dead-block headroom before 2× compaction trigger | up to 1× live | (transient) |

## 5. `.emdb.vec` sidecar (per ADR-010, 384-dim MiniLM)

| Emails | Float32 vectors | SQ8 vectors | HNSW graph | Sidecar total (SQ8) |
|--------|----------------|-------------|------------|---------------------|
| 100K | 154 MB | 38 MB | 15 MB | ~55 MB |
| 1M | 1.5 GB | 384 MB | 150 MB | ~0.55 GB |
| 10M | 14.7 GB | 3.7 GB | 1.5 GB | ~5.2 GB |

## 6. Write amplification (steady state)

| Operation | Blocks written |
|-----------|---------------|
| Add email | 2 content blocks + 1 delta block share + WAL share ≈ 3 |
| Index flush (batch of ~83) | ~10–15 nodes + IndexRoots + Checkpoint |
| Delta compile (500 ops) | ~7 pages + 1 directory |
| Checkpoint | Location-index delta paths (log n) + 1 Checkpoint block |

Rule of thumb: bytes written ≈ 1.1–1.3 × bytes ingested between compactions; a compaction cycle at the 2× trigger adds one more full pass of live data.
