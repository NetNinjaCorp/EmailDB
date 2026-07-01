---
acceptance_criteria:
- ScanExistingBlocks returns Task instead of async void
- FileStream reference swap is safe with proper disposal
- MemoryMappedFile disposed in all code paths
- CapnProto duplicate EnterWriteLock fixed
created: '2026-02-22'
epic_id: EPIC-EMDB-1
id: US-EMDB-3
points: 3
priority: must
status: done
tags: []
title: Fix RawBlockManager async void and resource safety
updated: '2026-02-22'
---

As a developer, I want RawBlockManager to be free of async anti-patterns so that it is reliable and doesn't lose exceptions.

ScanExistingBlocks uses async void (non-event handler), FileStream reference swapping is dangerous, and MemoryMappedFile disposal isn't handled in all paths. Also the CapnProto version has a duplicate EnterWriteLock bug.