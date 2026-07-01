---
acceptance_criteria:
- Point lookup returns correct LeafEntry for existing key
- Point lookup returns not-found for missing key
- Lookup works on single-level tree (height 1)
- Lookup works on multi-level tree (height 2+)
- Lookup reads at most tree-height blocks
- Concurrent readers do not block each other
- Node reads integrate with CacheManager when available
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-27
points: 5
priority: must
status: done
tags: []
title: Implement B+-tree lookup and range query
updated: '2026-02-23'
---

As a developer, I want BTreeIndex.LookupAsync to perform read-only point queries so that the WAL manager can fall through to the BTree after checking its buffer.

Add to `EmailDB.Format/BTreeIndex.cs`:

**LookupAsync(EmailHashedID key, CancellationToken)**:
- If tree is empty (_currentRoot == null), return not-found
- Navigate from root to leaf using binary search on internal node keys
- At each internal node, compare key against Keys[] to find the correct child offset, read child via ReadNodeBlockAtOffsetAsync
- At leaf, scan Entries[] for matching key
- Return matching LeafEntry or not-found Result

This is the read-only counterpart to InsertAsync — it follows the same root-to-leaf path but never writes. The navigation logic can be extracted from the existing insert path for reuse.

Wire into BTreeWALManager.LookupAsync as the fallback after checking the in-memory buffer.

Depends on: US-EMDB-26 (insert with splits must work for tree to exist)