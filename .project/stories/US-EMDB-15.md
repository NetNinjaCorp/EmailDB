---
acceptance_criteria:
- Benchmark for Protobuf vs JSON serialization of each content type
- Benchmark for block write throughput at various sizes
- Benchmark for block read with and without cache
- Benchmark results logged in a reproducible format
created: '2026-02-22'
epic_id: EPIC-EMDB-6
id: US-EMDB-15
points: 3
priority: could
status: backlog
tags: []
title: Expand benchmark suite for serialization, block I/O, and SQLite comparison
updated: '2026-02-22'
---

Partially complete: EmailDB.Benchmark.SQLite project built with BenchmarkDotNet comparing SQLite+FTS5 vs EmailDB across bulk insert, single insert, retrieval, search, mutation, and storage size at 1K/10K/100K scale. Remaining: serialization format benchmarks (Protobuf vs JSON), block write throughput at various sizes, block read with/without cache.