---
acceptance_criteria:
- Insert splits propagate upward and grow height at root split
- Delete rebalances leaves and internal nodes and collapses single-child roots
- Range scans return sorted results across leaf boundaries via parent backtracking
- Old tree roots remain fully readable after mutations
- Randomized insert/delete stress against a reference model at 1M+ entries
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-13
id: US-EMDB-68
points: 13
priority: must
status: done
tags:
- v3
- btree
- cow
title: COW insert, delete, and range scan
updated: '2026-07-04'
---

As the index engine, I want copy-on-write mutations with full rebalancing so that the tree stays balanced and old roots remain consistent snapshots (docs/BTree_Index.md Sections 4-5). Root-to-leaf path rewrite, splits with promotion, leaf AND internal underflow handling, root collapse, parent-backtrack range scans (no sibling pointers).