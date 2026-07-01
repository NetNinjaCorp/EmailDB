---
created: '2026-02-22'
id: EPIC-EMDB-7
points: null
priority: must
status: archived
tags:
- search
- embeddings
- performance
target_date: null
title: Embedding Search
updated: '2026-07-01'
---

Implement embedding-based semantic search to close EmailDB's only performance gap vs SQLite. Benchmarks showed EmailDB's linear scan is ~49x slower than SQLite FTS5 at 100K emails.

**Architecture**: Sidecar `.emdb.vec` files store embeddings + HNSW index alongside each .emdb shard (ADR-009). Each shard's vector index is self-contained. Search fans out across shards and merges results. Web frontend only needs .vec files to serve search.

**Scaling strategy** (ADR-010): Phased HNSW — Phase 1: float32 in-memory (<1M), Phase 2: SQ8 quantized (1-10M), Phase 3: SQ8 + mmap (10-50M), Phase 4: PQ/DiskANN (50M+).

**Embedding model**: all-MiniLM-L6-v2 (384 dims, ~80MB ONNX). Local/offline via ONNX Runtime.

**Decoupled from ZoneTree**: Vector index is independent — no dependency on EPIC-EMDB-3 ZoneTree adapter work.