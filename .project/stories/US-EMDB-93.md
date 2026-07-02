---
acceptance_criteria:
- Date-range query returns exactly the emails in range without scanning pages
- Duplicate timestamps handled via the BlockId suffix
- Index maintained on add and delete and recovered via Checkpoint
- Combines as a pre-filter with other search phases
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-19
id: US-EMDB-93
points: 5
priority: should
status: backlog
tags:
- v3
- search
- date-index
title: Date BTree secondary index
updated: '2026-07-02'
---

As a user, I want time-range queries served by a date index (docs/BTree_Index.md Section 7, IndexKind 2): composite DateTicks|BlockId keys for uniqueness, empty values, maintained on add/delete, registered in the Checkpoint.