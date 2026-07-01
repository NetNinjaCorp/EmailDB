---
acceptance_criteria:
- SearchAsync returns semantically relevant results ranked by similarity
- Search at 100K emails completes in <5ms (Phase 1 float32 target)
- Top-K results configurable
- IVectorIndex interface abstraction for future SQ8/mmap phases
- Cross-shard search merges results from multiple .vec sidecars
- HNSW graph persisted to .vec sidecar and restored on open
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-18
points: 8
priority: must
status: backlog
tags: []
title: Implement HNSW search with phased scaling path
updated: '2026-02-22'
---

Implement Phase 1 of ADR-010: float32 HNSW in memory using HnswLite (zero deps, .NET 8 compatible) or curiosity-ai HNSW (v1.0.x for .NET 9 compat). Design the search API so the underlying index is swappable — Phase 2 (SQ8) and Phase 3 (mmap) should be drop-in replacements behind an IVectorIndex interface. HNSW graph serialized to .vec sidecar via MessagePack or custom binary format. Cross-shard search: fan out to each shard's index, merge top-K results by similarity score.