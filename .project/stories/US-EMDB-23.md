---
acceptance_criteria:
- SQ8 quantization reduces vector memory by ~4x
- Recall at 100K emails >= 98% vs float32 baseline
- SIMD int8 distance function implemented via System.Runtime.Intrinsics
- Calibration parameters persisted in .vec sidecar
- IVectorIndex interface allows transparent swap between float32 and SQ8
- 'Benchmark: SQ8 search latency at 1M emails < 3ms'
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-23
points: 5
priority: should
status: backlog
tags: []
title: Implement SQ8 scalar quantization for HNSW (Phase 2)
updated: '2026-02-22'
---

As a developer, I want scalar quantization (float32 to int8) so that the vector index uses 4x less memory and can scale to 10M emails per shard. Implement per-dimension min/max calibration, quantize/dequantize routines, and SIMD-accelerated int8 distance computation via System.Runtime.Intrinsics. Integrate as a drop-in behind the IVectorIndex interface from US-EMDB-18. Store calibration parameters in .vec sidecar header.