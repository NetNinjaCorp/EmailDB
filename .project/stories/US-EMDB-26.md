---
acceptance_criteria:
- Single insert into empty tree creates root leaf node
- Insert into full leaf triggers split producing two leaves and a new internal root
- Cascade split through 3 levels produces correct tree structure
- Inserting duplicate key updates value without creating duplicate entry
- Old node blocks remain in file untouched after insert (append-only verified)
- Tree maintains sorted key order after 10K random inserts
- All new blocks written via RawBlockManager.WriteBlockAsync
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-26
points: 8
priority: must
status: done
tags: []
title: Implement B+-tree insert with copy-on-write node splitting
updated: '2026-02-23'
---

As a storage engine developer, I want to insert key-value pairs into the B+-tree using append-only copy-on-write semantics so that the index grows without modifying existing blocks.

**Scope**:
- Implement leaf node insert with sorted key placement
- Implement leaf split when EntryCount exceeds capacity (split at midpoint, create two new leaf blocks)
- Implement parent cascade: update parent internal node with new separator key and child offsets → new parent block
- Implement recursive split cascade up to root (new root created if current root splits)
- All new/modified nodes appended as blocks via RawBlockManager (never overwrite)
- Old node blocks become dead space (tracked for compaction)
- Handle duplicate key detection (upsert semantics — update value if key exists)
- Maintain in-memory "dirty node" set during a mutation batch before flushing

**CouchDB model**: No sibling pointers. Only the root-to-leaf path is rewritten per mutation. Unchanged subtrees are shared between old and new tree versions.