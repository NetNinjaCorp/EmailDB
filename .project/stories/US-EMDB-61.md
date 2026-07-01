---
acceptance_criteria:
- SoftDeleteEmailAsync removes email from source folder and adds to Dead folder
- BTree entry persists after soft-delete (lookup still succeeds)
- BTree.EntryCount unchanged after soft-delete
- Content and metadata blocks remain readable after soft-delete
- Dead folder created automatically during file initialization
- HardDeleteEmailAsync removes from BTree and marks for compaction reclamation
- Soft-deleted email can be recovered by moving out of Dead folder
created: '2026-02-25'
epic_id: EPIC-EMDB-11
id: US-EMDB-61
points: 3
priority: must
status: backlog
tags: []
title: Implement soft-delete via Dead folder and optional hard delete
updated: '2026-02-25'
---

As a developer, I want deleting an email to move it to a Dead folder instead of removing it from the BTree so that the BTree is a permanent append-once index and "deleted" emails can be recovered.

**Soft-delete flow (default):**
1. Remove EmailHashedID from current folder's EmailIds list
2. Add EmailHashedID to Dead folder's EmailIds list
3. BTree entry is NOT modified — email remains indexed
4. Content block and metadata block remain on disk untouched

**Hard delete flow (optional, explicit):**
1. Optionally set a tombstone flag on the metadata block (write new metadata block with flag, BTree copy-on-write updates pointer)
2. Or remove from BTree entirely (using existing DeleteAsync)
3. Content and old metadata blocks become dead (reclaimable by compaction)

**Dead folder:**
- System folder "Dead" created automatically during file initialization
- Not visible to normal folder listings (filtered by convention or flag)
- Can be listed explicitly for recovery purposes

**Key invariant:** BTree.EntryCount always equals total emails ever ingested (not reduced by soft-delete).