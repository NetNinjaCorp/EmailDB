---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-96-5
id: US-EMDB-96-6
points: 5
status: todo
story_id: US-EMDB-96
tags: []
title: Implement HNSW build/search with sidecar flush
updated: '2026-07-02'
---

In-memory float32 HNSW, insert on add, dot-product search, AsyncReaderWriterLock (searches never block on inserts), flush on close + periodic checkpoint.