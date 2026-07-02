---
acceptance_criteria:
- Conceptual queries return semantically related emails
- End-to-end search under 20ms at 100K emails including query embedding
- Graph flushes to sidecar on close and periodic checkpoint
- Search never blocks on concurrent inserts
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-20
id: US-EMDB-96
points: 13
priority: could
status: backlog
tags:
- v3
- vectors
- hnsw
- embeddings
title: Phase-1 HNSW and embedding pipeline
updated: '2026-07-02'
---

As a user, I want semantic search (ADR-010 Phase 1, ADR-011): MiniLM-L6-v2 384-dim ONNX embeddings, Subject|From|body text prep truncated to 256 tokens, in-memory float32 HNSW with periodic sidecar flush, cosine-via-dot-product.