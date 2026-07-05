---
assignee: claude
created: '2026-07-05'
depends_on: []
id: US-EMDB-104-5
points: 5
status: done
story_id: US-EMDB-104
tags: []
title: Implement one-pass COW batch apply in CowBTree.PutBatch
updated: '2026-07-05'
---

Rewrite CowBTree.PutBatch to apply a sorted batch in a single COW pass: group entries by target leaf, rewrite each touched leaf and internal path once, propagate splits once. Target docs/BTree_Index.md §4 write-amplification (~15 nodes for 100 inserts, not 400). Safety-critical: must pass the full CowBTree model-stress and Merkle verification suites unchanged. Un-skip the xfail test LocationIndexCheckpointTests.Checkpoint_batch_insert_is_one_cow_pass_not_n_individual_inserts and remove the current-behavior note from docs/BTree_Index.md.