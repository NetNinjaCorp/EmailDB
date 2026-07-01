---
acceptance_criteria:
- Empty tree bulk insert builds tree with optimally packed leaves (near MaxEntries
  per leaf)
- 10000 sorted entries produce correct tree height for the branching factor
- All leaves contain entries in globally sorted order
- Merge with existing tree preserves all old entries plus new entries
- Duplicate keys across old tree and new entries are handled as upserts
- Bulk insert of 100K entries produces correct EntryCount in IndexRoot
- Single IndexRoot written per bulk insert (not one per entry)
- Tree built by bulk insert passes Merkle hash verification
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-37
points: 8
priority: must
status: backlog
tags: []
title: Implement BTreeIndex.BulkInsertAsync for optimal leaf packing
updated: '2026-02-23'
---

As a developer, I want BTreeIndex.BulkInsertAsync to build or rebuild the BTree bottom-up from a sorted array of entries so that WAL flushes and file rebuilds produce optimally packed trees with zero dead blocks.

Add to `EmailDB.Format/BTreeIndex.cs`:

**BulkInsertAsync(LeafEntry[] sortedEntries, CancellationToken)**:

Empty tree (bottom-up build):
1. Partition sortedEntries into chunks of 82 (BTreeLeafNode.MaxEntries)
2. For each chunk: create BTreeLeafNode, compute hash, serialize, write via RawBlockManager
3. Collect (offset, hash, firstKey) for each written leaf
4. Build internal nodes bottom-up: group leaf refs into chunks of 55 (MaxChildren), create BTreeInternalNode with separator keys (first key of each child except leftmost), write
5. Repeat upward until single root remains
6. Write IndexRoot pointing to root node

Non-empty tree (merge path):
1. Traverse existing tree to collect all LeafEntry values from all leaves
2. Merge-sort existing entries with new sorted entries (duplicate keys = upsert)
3. Build fresh tree bottom-up from merged set (same as empty tree path)

BulkInsertAsync is used by BTreeWALManager.FlushAsync when entry count exceeds a threshold (~500+), and by BTreeRebuildManager for full file rebuilds.

Depends on: US-EMDB-26 (core insert/split), US-EMDB-27 (lookup for leaf traversal)