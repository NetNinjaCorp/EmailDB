---
acceptance_criteria:
- MetadataManager uses SemaphoreSlim or AsyncReaderWriterLock instead of object lock
- SegmentManager uses async-safe locking
- CacheManager LockAsync replaced with proper async lock pattern
- No Monitor.Enter usage in async code paths
- All existing unit tests still pass
created: '2026-02-22'
epic_id: EPIC-EMDB-1
id: US-EMDB-1
points: 5
priority: must
status: done
tags: []
title: Fix async/lock concurrency issues in managers
updated: '2026-02-22'
---

As a developer, I want the storage managers to use correct async concurrency patterns so that the system doesn't deadlock under load.

MetadataManager uses `object` lock with async code (potential deadlock). SegmentManager uses `object` lock for thread safety but no async variants. CacheManager has a LockAsync extension using Monitor.Enter with async which is an improper pattern. These need to be replaced with proper AsyncReaderWriterLock or SemaphoreSlim patterns consistent with RawBlockManager.