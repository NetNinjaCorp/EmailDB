---
acceptance_criteria:
- Delete removes key from lookup results
- Delete of non-existent key returns not-found without modifying tree
- Underflow triggers merge with sibling via parent
- Root collapse produces correct single-level tree
- EntryCount decremented correctly after delete
- Old leaf blocks remain in file (append-only)
- Concurrent delete and read operations are safe
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-28
points: 5
priority: must
status: done
tags: []
title: Implement B+-tree delete with tombstone and merge
updated: '2026-02-23'
---

As a storage engine developer, I want to delete keys from the B+-tree so that removed emails no longer appear in lookups.

**Scope**:
- Implement key deletion from leaf node (copy leaf without the key, append new leaf)
- Cascade parent updates (copy-on-write up to root)
- Implement node merge when leaf drops below minimum fill (merge with logical sibling via parent backtrack)
- Handle root collapse when root has single child after merge
- Update IndexRoot.EntryCount on delete
- Deleted email content blocks tracked in MetadataContent.OutdatedOffsets for compaction

**Design choice**: Immediate delete (remove from leaf copy) rather than tombstone markers. Since we're already copy-on-write, the old leaf with the entry still exists as dead space — no tombstone needed. Compaction reclaims space.