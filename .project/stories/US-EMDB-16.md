---
acceptance_criteria:
- Embedding model selected and documented in ADR
- Model can generate embeddings for email subject+body+from fields
- Embedding generation benchmarked (throughput at 1K and 10K emails)
- NuGet dependencies added to EmailDB.Format.csproj
- Model distribution strategy decided (sidecar ONNX file vs embedded resource)
- Async/batched embedding generation API implemented
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-16
points: 5
priority: must
status: backlog
tags: []
title: Choose and integrate embedding model
updated: '2026-02-22'
---

Select all-MiniLM-L6-v2 (384 dims) as the embedding model. Integrate via ONNX Runtime for fully offline operation. Evaluate library options: (A) Curiosity-AI SentenceTransformers — .NET 9.0 native, pairs with their HNSW lib, (B) SmartComponents.LocalEmbeddings — Microsoft-backed, includes built-in brute-force, (C) Direct ONNX Runtime + FastBertTokenizer — maximum control, minimal deps. Must handle model distribution (sidecar ONNX file or embedded resource). Benchmark embedding throughput — at 1-15ms per email, bulk indexing 10M emails = 3-42 hours on CPU, so batching and async generation are critical.