---
acceptance_criteria:
- Silent catch blocks either propagate errors via Result or log them
- Console.WriteLine calls replaced with ILogger or similar abstraction
- Error context preserved in all failure paths
created: '2026-02-22'
epic_id: EPIC-EMDB-1
id: US-EMDB-2
points: 3
priority: must
status: done
tags: []
title: Fix silent error handling in CacheManager
updated: '2026-02-22'
---

As a developer, I want CacheManager to properly propagate or log errors so that failures are visible and debuggable.

Multiple catch blocks in CacheManager silently swallow exceptions. Console.WriteLine is used for error output in RawBlockManager. Need to implement proper error handling — either propagate via Result pattern or add a logging interface.