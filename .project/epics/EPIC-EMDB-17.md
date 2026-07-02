---
created: '2026-07-01'
id: EPIC-EMDB-17
points: null
priority: must
status: draft
tags:
- v3
- api
- email
target_date: null
title: EmailManager API
updated: '2026-07-01'
---

The public API layer composing the whole v3 stack: create/open/close lifecycle (superblock, encryption bootstrap, checkpoint load, index roots, WAL replay), AddEmail (hash, Tier 2/3 blocks, indexes, folder delta, WAL), GetEmail (primary index to location index to content), move/delete/flag operations, and folder listing. Replaces the fully-commented-out v1 EmailManager. Success: end-to-end add/list/open/get/move/delete flows work through one coherent API with group-commit batching.