---
created: '2026-07-01'
id: EPIC-EMDB-12
points: null
priority: must
status: draft
tags:
- v3
- core
- format
- foundation
target_date: null
title: v3 BlockStore Foundation
updated: '2026-07-01'
---

Implement the v3 on-disk foundation per EmailDB_FileFormat_Spec.md Sections 2-5: dual-slot superblock (feature flags, CleanShutdown, encryption bootstrap fields, checkpoint pointer), 96-byte block format with ULID BlockIds and BLAKE3-128 checksums, length-sanity validation, monotonic ULID generation, single-writer OS lock, fsync-is-fatal discipline, runtime block map and scan fallback. Success: a v3 file can be created, appended to, reopened, and survives torn-write fault injection. Replaces the v1 RawBlockManager (int64 IDs, OverrideLocation, 36B header).