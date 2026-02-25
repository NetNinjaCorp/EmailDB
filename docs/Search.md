# Search

Search capabilities are implemented in phases, from structured queries using existing BTree infrastructure to full-text and semantic search in later phases. Full-text and semantic search indexes are stored in a **separate file** alongside the main `.emdb` file.

## Phase 1: Secondary BTree Indexes

Reuse the existing `BTreeIndex` infrastructure to create additional trees keyed on searchable fields:

| Index | Key | Value | Use Case |
|-------|-----|-------|----------|
| DateIndex | date ticks (8B) | EmailHashedID | "Emails from last week" |
| SenderIndex | BLAKE3(normalized_sender) | EmailHashedID | "Emails from alice@" |
| FolderDateIndex | BLAKE3(folder + date) | EmailHashedID | Folder sorted by date |

Each secondary index gets its own `IndexRoot` block, tracked in `MetadataContent`.

**Performance at 10M emails:**
- Exact sender lookup: O(log N) = 3-4 node reads
- Date range query: O(log N + K) where K = result count
- Combined queries: intersect results from two trees

**Index size:** ~460 MB per secondary BTree at 10M emails.

**Write cost:** One COW path rewrite per secondary BTree per email insert (12-16 blocks total across all trees).

## Phase 2: Listing Page Scan

Folder listing pages contain subject and sender strings. For queries like "emails about invoice":

- Scan listing pages' subject fields sequentially
- Folder-scoped scan (50K emails): ~10 MB, ~10ms on NVMe
- Full scan (10M emails): ~2 GB, 1-2 seconds on NVMe

This is the "80% solution" for subject/sender text search with no additional index.

## Phase 3: Full-Text Search (Separate File)

For email body text search, a segment-based inverted index stored in a **separate search file**:

| Block Type | Purpose |
|------------|---------|
| FTSSegmentMeta (15) | Segment metadata (field list, doc count) |
| FTSTermDictionary (16) | FST-encoded term-to-posting-list map |
| FTSPostingList (17) | Compressed posting lists |
| FTSSearchRoot (18) | Root pointer for all active segments |

Each segment is immutable -- a natural fit for append-only storage. New emails batch into new segments. Searching merges across segments. Compaction merges segments.

**Performance at 10M emails:** ~15-30 block reads per single-term query. With caching: 2-5 reads.

**Index size:** 15-25% of raw text size. At 10M emails (~50 GB text): ~7.5-12.5 GB.

## Phase 4: Bloom Filters (Separate File)

Per-folder or per-segment bloom filters for quick elimination:

| Block Type | Purpose |
|------------|---------|
| BloomFilter (19) | Probabilistic existence filter |

- 1% false positive rate: ~10 bits per element
- 10M emails: ~12 MB total

## Phase 5: Vector Embeddings (Separate File)

Semantic search via embeddings (768-1536 floats per email):

| Block Type | Purpose |
|------------|---------|
| EmbeddingContent (20) | Vector embedding data per email |
| VectorIndexNode (21) | HNSW/IVF index tree nodes |
| VectorIndexRoot (22) | Vector index root pointer |

Complements structured search:

| Structured search wins | Semantic search wins |
|------------------------|---------------------|
| "from:alice@example.com" | "emails about the project delay" |
| "invoice #12345" | "that thing Bob sent about the conference" |
| Date range queries | Cross-language matching |

## Encryption Policy for Search Blocks

| Block Type | Encrypted? | Rationale |
|------------|------------|-----------|
| Secondary BTree nodes | No | Keys are BLAKE3 hashes (opaque) |
| FTS blocks | Yes | Term dictionaries expose search terms |
| Bloom filters | No | Opaque bit arrays |
| Embedding/vector blocks | Yes | Embeddings can leak content |
