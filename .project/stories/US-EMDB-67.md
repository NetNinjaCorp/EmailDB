---
acceptance_criteria:
- Leaf and internal nodes round-trip for IndexKinds 0/1/2 layouts
- Deserialization bounds-checks EntryCount against payload size and rejects overflow
- 'Capacities match spec: 83-entry primary leaves with 50-way internals and 124-entry
  location leaves with 71-way internals'
- NodeContentHash computed over full serialized payload
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-13
id: US-EMDB-67
points: 5
priority: must
status: backlog
tags:
- v3
- btree
- serialization
title: Generic node serialization (declared key/value sizes)
updated: '2026-07-01'
---

As the index engine, I want one node format with a 12-byte header declaring IndexKind/KeySize/ValueSize (spec Section 6) so that every index shares block types 4/5 and future indexes need no format change.