---
acceptance_criteria:
- InitializeNewFile does not corrupt currentPosition in RawBlockManager
- OverrideLocation writes restore original position after seeking
- System blocks (WAL/FolderTree/Metadata) not overwritten after InitializeNewFile
- 'Integration test: InitializeNewFile followed by email insert reads back correctly'
created: '2026-02-22'
epic_id: EPIC-EMDB-1
id: US-EMDB-22
points: 3
priority: must
status: done
tags: []
title: Fix CacheManager.InitializeNewFile corrupting RawBlockManager position
updated: '2026-02-22'
---

As a developer, I want CacheManager.InitializeNewFile to work without corrupting file state so that initialization is safe. Bug found during SQLite benchmarking: InitializeNewFile calls WriteBlockAsync with OverrideLocation:0 to rewrite the header. This resets RawBlockManager's currentPosition to just past the header (~110 bytes), but WAL/FolderTree/Metadata blocks exist beyond that. Subsequent writes overwrite those system blocks. Location: CacheManager.cs InitializeNewFile (~line 1017), RawBlockManager.cs WriteBlockAsync (~line 110).