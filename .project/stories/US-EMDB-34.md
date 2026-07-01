---
acceptance_criteria:
- Insert throughput benchmarked at 4 scale points with results documented
- Point lookup latency benchmarked at 4 scale points
- Range query throughput measured
- WAL batch size vs flush latency curve produced
- Concurrent read/write stress test passes without corruption
- Crash recovery stress test passes 100/100 trials
- Space amplification metrics documented with and without compaction
- Results added to benchmark_results.txt
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-34
points: 5
priority: should
status: done
tags: []
title: B+-tree performance benchmarks and stress testing
updated: '2026-02-24'
---

As a storage engine developer, I want comprehensive benchmarks proving the B+-tree meets performance targets at scale so that we have confidence before building the search layer on top.

**Scope**:
- Benchmark insert throughput: 1K, 10K, 100K, 1M entries (single and batch)
- Benchmark point lookup latency: at 1K, 10K, 100K, 1M tree sizes
- Benchmark range query throughput: 100-entry ranges at various tree sizes
- Benchmark WAL flush latency vs batch size (10, 100, 500, 1000)
- Benchmark compaction time vs file size / dead space ratio
- Stress test: concurrent readers + writer (10 reader threads, 1 writer thread)
- Stress test: crash recovery simulation (kill process mid-flush, verify recovery)
- Measure space amplification over time (with and without compaction)
- Measure hash chain verification time (quick, standard, full modes)
- Compare against the SQLite benchmark suite for equivalent operations
- Add to EmailDB.Benchmark.SQLite project or create dedicated benchmark project

**Targets**:
- Single point lookup: < 1ms at 1M entries (with cache warm)
- Batch insert (100 entries): < 10ms flush time
- Full integrity verification: < 30s at 1M entries