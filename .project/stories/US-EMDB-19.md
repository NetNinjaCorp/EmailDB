---
acceptance_criteria:
- Inserting an email queues async embedding generation
- Deleting an email removes its vector from the index
- Bulk insert batches embedding generation efficiently
- 'Cold start: missing .vec triggers background rebuild from .emdb'
- Model version tracked in .vec header for upgrade detection
- Index consistency verified after insert/delete/move sequences
created: '2026-02-22'
epic_id: EPIC-EMDB-7
id: US-EMDB-19
points: 5
priority: must
status: backlog
tags: []
title: Auto-index emails on insert and maintain index consistency
updated: '2026-02-22'
---

Wire embedding generation into the email insert/delete/move pipeline. Embedding generation is the bottleneck (1-15ms per email on CPU), so this must be async and batched. On insert: queue email for embedding, generate in background, add to HNSW index, persist to .vec sidecar. On delete: remove vector from index. On move: no vector change needed (embeddings are content-based, not folder-based). Handle cold start: when opening an .emdb with no .vec sidecar, queue all emails for background embedding generation. Handle model upgrade: regenerate all embeddings when model version changes.