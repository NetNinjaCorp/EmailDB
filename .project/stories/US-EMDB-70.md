---
acceptance_criteria:
- Flush triggers on count threshold and time threshold and explicit call
- Buffered entries sorted by key and applied in one COW pass
- IndexRoot Sequence increments monotonically per index
- Failed node or root write leaves previous root authoritative with orphans reclaimed
  later
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-13
id: US-EMDB-70
points: 5
priority: must
status: backlog
tags:
- v3
- btree
- wal
title: IndexRoot and WAL-buffered flush
updated: '2026-07-01'
---

As the index engine, I want IndexRoot descriptors (68 B: IndexKind, RootBlockId, EntryCount, TreeHeight, RootHash, Sequence) and batched WAL-buffered flushes so that writes amortize and a failed flush is harmless (docs/BTree_Index.md Sections 4, 6).