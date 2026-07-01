---
acceptance_criteria:
- Benchmark added to EmailDB.Benchmark.SQLite for embedding search
- Results show embedding search < 50ms at 100K emails
- Comparison table updated with embedding search vs FTS5
- Results documented in benchmark_results.txt
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-20
points: 3
priority: should
status: backlog
tags: []
title: Benchmark embedding search vs SQLite FTS5
updated: '2026-02-22'
---

Extend the EmailDB.Benchmark.SQLite suite to include HNSW embedding search. Compare: (1) SQLite FTS5, (2) EmailDB linear scan (existing), (3) EmailDB HNSW search (new). Benchmark at 1K, 10K, 100K scale. Also benchmark embedding generation throughput and .vec sidecar file sizes. Target: HNSW search < 5ms at 100K (beating SQLite FTS5 31ms by 6x).