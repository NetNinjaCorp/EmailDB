---
acceptance_criteria:
- EmailDbStore uses BLAKE3-128 for checksum computation
- Crc32.NET removed from benchmark csproj
- Blake3 added to benchmark csproj
- BlockOverhead constant updated
- Benchmark compiles and runs correctly
created: '2026-02-24'
epic_id: EPIC-EMDB-10
id: US-EMDB-51
points: 2
priority: should
status: done
tags: []
title: Update benchmark project for BLAKE3-128 checksums
updated: '2026-02-24'
---

As a developer, I want the EmailDB.Benchmark.SQLite project to use BLAKE3-128 checksums so benchmarks reflect the actual block format.

Changes:
- EmailDB.Benchmark.SQLite/Stores/EmailDbStore.cs: Replace Force.Crc32.Crc32Algorithm.Compute() with Blake3.Hasher.Hash() truncated to 16 bytes. Update BlockOverhead constant (8+4+4=16 → 8+4+16=28 or recalculate based on actual format used in benchmark).
- EmailDB.Benchmark.SQLite.csproj: Replace Crc32.NET package reference with Blake3.
- EmailDB.Testing.FileFormatBenchmark: Update any CRC32 references.