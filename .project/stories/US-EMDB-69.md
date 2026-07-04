---
acceptance_criteria:
- Every traversed node verified against its parent ChildHash and root against IndexRoot.RootHash
- Verify-on-cache-load allows cached nodes to skip re-hashing
- Full-tree verification mode walks all nodes
- Any single bit flip in any node fails the affected lookups with the contracted error
- Mismatch falls back per corruption contract
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-13
id: US-EMDB-69
points: 8
priority: must
status: done
tags:
- v3
- btree
- integrity
- merkle
title: Merkle integrity verification (path + full)
updated: '2026-07-04'
---

As the index engine, I want mandatory read-time Merkle verification so that index tampering and corruption are detected on the path traversed (spec Section 6, docs/BTree_Index.md Section 5). Write-only hashing is non-conforming.