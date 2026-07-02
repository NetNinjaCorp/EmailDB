---
created: '2026-07-01'
id: EPIC-EMDB-18
points: null
priority: should
status: draft
tags:
- v3
- compaction
- maintenance
target_date: null
title: Compaction
updated: '2026-07-01'
---

Space reclamation per docs/Compaction.md: incremental live/dead byte accounting with Cleanup blocks and Checkpoint counters (no scanning to decide), full-file side-file compaction with atomic rename + directory fsync (crash yields old-complete or new-complete), BlockLocationIndex rebuild during copy, optional re-encryption to the active epoch with DEK pruning. Success: fault injection at every swap step never yields a hybrid file; ULID pointers mean zero logical rewrites.