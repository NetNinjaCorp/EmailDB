---
acceptance_criteria:
- Sidecar .emdb.vec file created alongside .emdb on first insert
- Embeddings persisted and restored across close/reopen
- Email ID to vector index mapping maintained
- File format versioned for future quantization support
- Sidecar can be deleted and rebuilt from .emdb data
- Storage overhead benchmarked at 1K/10K/100K emails
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-17
points: 8
priority: must
status: backlog
tags: []
title: Implement .vec sidecar file format for vector storage
updated: '2026-02-22'
---

Design and implement the `.emdb.vec` sidecar file format (ADR-009). The sidecar stores: (1) serialized HNSW graph, (2) raw/quantized embedding vectors, (3) mapping from vector index positions to email IDs. Must support: create on first email insert, incremental updates (add/remove vectors), full rebuild from .emdb data, persistence across close/reopen. File format should be versioned to support future quantization changes (Phase 2: SQ8, Phase 3: mmap). No dependency on ZoneTree — this is a standalone binary format.