---
acceptance_criteria:
- Xfail test Checkpoint_batch_insert_is_one_cow_pass_not_n_individual_inserts un-skipped
  and passing
- Batch of N sorted inserts rewrites each touched node path once not per-entry
- CowBTree model stress and Merkle verification suites pass unchanged
- docs/BTree_Index.md current-behavior note removed
created: '2026-07-05'
depends_on: []
epic_id: EPIC-EMDB-13
id: US-EMDB-104
points: 5
priority: should
status: done
tags:
- v3
- btree
- performance
- write-amplification
title: One-pass amortized bulk load for PutBatch
updated: '2026-07-05'
---

As the storage engine, I want CowBTree.PutBatch to apply a sorted batch in a single COW pass (shared leaves and internal paths rewritten once, splits propagated once) so that checkpoint-time batch inserts meet the docs/BTree_Index.md §4 write-amplification target (~15 nodes for 100 inserts, not 400) instead of N individual inserts. Current behavior is correct but unamortized: a 30-entry batch writes 96 node blocks, same as 30 single Puts (measured in US-EMDB-71-1). The executable spec already exists as the skipped xfail test LocationIndexCheckpointTests.Checkpoint_batch_insert_is_one_cow_pass_not_n_individual_inserts — un-skip it as part of this story. Safety note: this touches the most safety-critical structure in the file; must be validated against the full CowBTree model-stress and Merkle verification suites before acceptance.