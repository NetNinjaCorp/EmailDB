---
created: '2026-07-01'
id: EPIC-EMDB-20
points: null
priority: could
status: draft
tags:
- v3
- search
- vectors
- embeddings
- bloom
target_date: null
title: Vector Search &amp; Bloom Filters
updated: '2026-07-01'
---

Search phases 4-5: .emdb.vec sidecar format (types 19-21, CheckpointSequence staleness echo, rebuildable), phase-1 HNSW with MiniLM-L6-v2 embeddings and the ADR-011 text pipeline, per-folder bloom filters (type 18, always encrypted). Later HNSW phases (SQ8, mmap, PQ) per ADR-010 as follow-ups. Success: semantic search <20ms end-to-end at 100K emails; sidecar rebuilds from the main file.