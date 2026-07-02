---
created: '2026-07-01'
id: EPIC-EMDB-13
points: null
priority: must
status: draft
tags:
- v3
- btree
- index
- core
target_date: null
title: Generic B+-Tree Engine
updated: '2026-07-01'
---

Copy-on-write B+-tree engine using the v3 generic node format (spec Section 6): 12-byte node header with declared IndexKind/KeySize/ValueSize, COW insert/delete/range with leaf AND internal rebalancing, mandatory Merkle path verification on read (write-only hashing is non-conforming), IndexRoot with Sequence, WAL-buffered batch flush. One engine serves PrimaryEmail, BlockLocation, Date, and FTS indexes. Success: 10M-entry tree with verified lookups in <=5 block reads and passing tamper-detection tests. See docs/BTree_Index.md.