# Search (v3)

Five complementary strategies, ordered by implementation priority. Block types use v3 numbering; layouts are normative in the [File Format Spec](../EmailDB_FileFormat_Spec.md).

## Phase 1 — Address trigram index (FTS blocks 14–17)

Instant substring matching on From/To/Cc addresses ("rya" → ryan@biztactix.com.au).

- Combined index over all address fields; postings distinguish the field
- Structure: FTSSegmentMeta (14) describes a segment; FTSTermDictionary (15) maps trigram → posting-list block; FTSPostingList (16) holds sorted EmailHashedID lists; FTSSearchRoot (17) is the root, registered in the Checkpoint's SecondaryIndexes table (IndexKind 3)
- **Always encrypted under every policy** — trigrams are trivially reversible to the indexed text, so plaintext trigrams would leak addresses even in an "encrypted" file
- Query: split query into trigrams → intersect posting lists → verify candidates against Tier 1 records (trigram match is necessary, not sufficient)

## Phase 2 — Listing page scan

Sequential scan of Tier 1 Subject/From/Preview fields — no extra index, works day one.

- Folder-scoped: a 50K-email folder is ~625 FolderPage blocks (~25 MB) → ~15 ms warm
- Whole-mailbox scan is the fallback when no better phase applies
- This is the accuracy backstop: other phases narrow candidates; page records confirm

## Phase 3 — Date BTree (IndexKind 2)

Secondary B+-tree keyed `DateTicks ‖ BlockId` for time-range queries ("last week", date-bounded searches). Combines with any other phase as a pre- or post-filter. See [BTree Index](BTree_Index.md) Section 7.

## Phase 4 — Vector embeddings (.emdb.vec sidecar, types 19–21)

Semantic search for conceptual/fuzzy queries ("meeting notes" finds "standup summary").

- EmbeddingContent (19) stores per-email vectors; VectorIndexNode (20) and VectorIndexRoot (21) hold the HNSW graph — all in the `.emdb.vec` sidecar, never the main file
- Sidecar is derived data: rebuildable from Tier 1/2/3; staleness detected by comparing the sidecar header's echoed `CheckpointSequence` against the main file (spec Section 15)
- Embedding pipeline and phased HNSW scaling (float32 → SQ8 → mmap → PQ) per ADR-010/011: MiniLM-L6-v2 384-dim, `"Subject | From | body"` truncated to 256 tokens, cosine via dot product
- Latency target: < 20 ms end-to-end at 100K emails including query embedding

## Phase 5 — Bloom filters (type 18)

Per-folder probabilistic filters for cheap elimination before page scans.

- One BloomFilter block per folder over its Tier 1 tokens; sized ~1% false-positive
- Multi-folder search consults filters first and scans only folders that might match
- Encrypted always (filter bits leak token presence)
- Root/catalog registered in SecondaryIndexes (IndexKind 4)

## Query planning

```
date-bounded?            → Phase 3 narrows first
looks like an address?   → Phase 1
folder-scoped keyword?   → Phase 5 eliminate → Phase 2 scan survivors
conceptual / free text?  → Phase 4, merge with Phase 2 for exact hits
```

All phases return `EmailHashedID`s; the primary index resolves them to content BlockIds, the BlockLocationIndex to physical locations. Search structures are all rebuildable — none participate in crash recovery guarantees.

## Encryption summary

| Structure | Policy |
|-----------|--------|
| FTS 14–17, BloomFilter 18 | Always encrypted (reversible to content) |
| Date BTree | Plaintext under Default (keys are timestamps + ULIDs), encrypted under Full |
| .vec sidecar | Own encryption story; embeddings are content-derived — encrypt at rest when the main file is encrypted (format TBD with the sidecar spec) |
