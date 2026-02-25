# Compaction

Compaction creates a new file containing only live blocks, reclaiming space from dead blocks left behind by the append-only, copy-on-write model.

## Sources of Dead Blocks

| Source | Frequency | Volume |
|--------|-----------|--------|
| BTree COW path rewrites | Every flush (~82 emails) | `tree_height` blocks per insert |
| Folder page rebuilds | On delta compile | Old page blocks replaced |
| Metadata/IndexRoot updates | Every mutation batch | 1-2 blocks per batch |
| Deleted emails | On delete | Tombstoned content blocks |

BTree nodes dominate dead block accumulation. During bulk import, dead BTree blocks can reach 150-300x the live index size before compaction.

## Tiered Compaction

### Level 1 -- BTree Node Compaction (frequent, lightweight)

- **Trigger:** Dead BTree blocks exceed 2x live node count
- **Action:** Rewrite only BTree nodes + IndexRoot
- **Cost at 10M emails:** Live BTree is ~494 MB, takes seconds

### Level 2 -- Listing Page Rebuild (periodic)

- **Trigger:** Delta log exceeds threshold, or part of compaction
- **Action:** Per-folder -- apply deltas to pages, re-sort, re-paginate
- **Effect:** Clears delta logs, consolidates fragmented pages

### Level 3 -- Full Compaction (rare)

- **Trigger:** Manual or file size exceeds 2x live data
- **Action:** Rewrite all live blocks into a new file
- **Effect:** Optimal disk layout restored

## Compaction and Encryption

With `reEncrypt = true`, only blocks being rewritten get re-encrypted with the active DEK. EmailContent blocks skipped by tiered compaction retain their original key epoch. The `KeyEpoch` field in the block header ensures correct DEK lookup at read time.

## Compaction and Offsets

BTree leaves store `BlockId` (ULID), not file offsets. After compaction, the in-memory `Dictionary<Ulid, long>` is rebuilt from the new file layout. No BTree leaf rewriting is needed.

## Archival Workflow

```
Import emails  -->  Compact once  -->  Read-mostly steady state
  (dead blocks       (reclaim all       (BTree is static,
   accumulate)        dead space)         no further amplification)
```

For archival use, dead block accumulation during bulk import is a one-time cost, fully reclaimed by a single compaction pass. In steady state, the BTree is essentially read-only with infrequent WAL flushes.

## Write Amplification During Bulk Import

| Emails | Avg Height | Dead BTree Before Compact | Live BTree After | Amplification |
|--------|-----------|---------------------------|------------------|---------------|
| 10K    | 2.5       | ~74 MB                    | ~511 KB          | ~149x         |
| 100K   | 3.0       | ~870 MB                   | ~4.9 MB          | ~177x         |
| 1M     | 3.7       | ~11 GB                    | ~49 MB           | ~230x         |
| 10M    | 4.0       | ~117 GB                   | ~494 MB          | ~242x         |

These dead blocks do not affect write throughput (writes are sequential appends) and are fully reclaimed by one compaction pass.

## Future: Batch-Aware Flush

The current flush inserts entries individually (82 separate COW rewrites). A batch-aware strategy would:

1. Sort entries by target leaf
2. Apply all inserts to each leaf in one pass
3. Propagate changes upward, creating each internal node once per batch

This would reduce per-flush amplification by ~3-5x (tree height factor).
