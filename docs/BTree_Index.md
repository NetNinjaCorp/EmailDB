# B+-Tree Indexes (v3)

Behavioral design for all B+-tree indexes in an `.emdb` file. Byte layouts are normative in the [File Format Spec](../EmailDB_FileFormat_Spec.md) Sections 6–7; this doc covers algorithms, protocols, and costs.

## 1. One node format, many indexes

Every tree uses the same generic node format (BlockTypes 4/5, IndexRoot 6). A node declares its `IndexKind`, `KeySize`, and `ValueSize` in its 12-byte node header, so adding an index never changes the file format.

| IndexKind | Index | Maps | Leaf entry | Capacity (leaf / internal children) |
|-----------|-------|------|------------|--------------------------------------|
| 0 | PrimaryEmail | EmailHashedID (SHA3-256) → BlockId | 32 + 16 = 48 B | 83 / 50 |
| 1 | BlockLocation | BlockId → (Offset, Length) | 16 + 16 = 32 B | 124 / 71 |
| 2 | Date | (DateTicks ‖ BlockId) → ∅ | 24 + 0 = 24 B | 166 / 55 |
| 3 | FTS | See [Search](Search.md) | | |

All trees share these properties:

- **Copy-on-write (CouchDB model).** A mutation rewrites only the root-to-leaf path as new blocks; unchanged subtrees are shared between tree versions. Old nodes become dead and are reclaimed by compaction.
- **No sibling pointers.** Range scans backtrack through the parent to reach the next leaf; this avoids cascade rewrites when a sibling splits.
- **Merkle integrity.** Internal child records embed `ChildHash` (BLAKE3-256 of the child's payload); the IndexRoot carries `RootHash`. Verification is mandatory at read time (Section 5).

## 2. Primary email index (IndexKind 0)

Maps `EmailHashedID` → `BlockId` of the EmailContent block. Values are ULIDs, never offsets — physical location is resolved through the BlockLocationIndex, which is what makes the tree compaction-safe.

### Capacity (50-way branching, 83-entry leaves)

| Height | Max entries | Use case |
|--------|-------------|----------|
| 1 | 83 | Tiny mailbox |
| 2 | 4,150 | Small |
| 3 | 207,500 | Medium |
| 4 | ~10.4M | Large (target) |
| 5 | ~519M | Beyond shard size |

A point lookup reads at most `height` node blocks; the top 2–3 levels (a few MB) stay cached, so steady-state lookups cost 1–2 uncached block reads.

## 3. BlockLocationIndex (IndexKind 1)

The indirection table: `BlockId → (Offset, Length)` for every live block. Full rationale in spec Section 7. Operational rules:

- **Offset-addressed internally** (child records carry `ChildOffset`, not `ChildBlockId`) — it cannot depend on itself. This is safe because it is derived data: compaction rebuilds it, and a full scan can always regenerate it.
- **Updated at checkpoint time**: entries for blocks appended since the last Checkpoint are sorted and batch-inserted; changed paths are written, then its IndexRoot, then the Checkpoint.
- **Resolution precedence**: runtime map (blocks since last checkpoint) → BlockLocationIndex → full scan (disaster only).
- Deletion happens implicitly at compaction rebuild; the index never carries tombstones.

## 4. Write path (primary index)

1. Inserts/deletes accumulate in the in-memory WAL buffer; each is also appended to a WAL block (BlockType 1) carrying the current `CheckpointBlockId` — durability comes from the WAL block, not the buffer.
2. Flush triggers: buffer reaches one leaf's worth (default 83), a time threshold, or an explicit call.
3. Flush: sort buffered entries by key → apply to the tree in one pass (COW; splits propagate upward, root split adds a level) → write all new nodes → fsync → write IndexRoot (Sequence + 1) → the batch commits at the next Checkpoint → clear buffer.
4. Write amplification: a batch of 100 inserts touching ~10 leaves writes ~15 nodes instead of 400.

Underflow on delete: leaves below minimum occupancy merge with or borrow from a neighbor; internal nodes rebalance the same way; a root with one child collapses (height − 1). Both leaf and internal rebalancing are required — leaf-only rebalancing degrades fill factor over time.

## 5. Verification modes

| Mode | What | Cost | When |
|------|------|------|------|
| Path (default) | Each traversed node's hash checked against parent's ChildHash; root against `IndexRoot.RootHash` | O(height), amortized ~0 with caching (verify on cache load) | Every read |
| Full | Walk entire tree verifying all hashes | O(nodes) | Integrity audit, post-recovery |

A conforming implementation MUST verify on the path it traverses. Verify-on-cache-load is sufficient: a node validated once against its parent may be served from cache without re-hashing. Mismatch behavior is defined in the spec's corruption contract (Section 13): fail the lookup, fall back to the previous Checkpoint's root, escalate to rebuild if persistent.

## 6. Recovery

The tree itself needs no scan-based recovery — the Checkpoint is authoritative:

1. Open finds the last valid Checkpoint (spec Section 10.2)
2. `PrimaryIndexRootBlockId` / `LocationIndexRootBlockId` load the roots
3. WAL blocks carrying that Checkpoint's BlockId are replayed into the WAL buffer and flushed
4. `IndexRoot.Sequence` (monotonic per index) exists only as a tiebreaker when no valid Checkpoint survives and roots must be recovered by scan

A failed IndexRoot or node write mid-flush is safe by construction: the previous root remains authoritative; orphaned new nodes are dead blocks reclaimed by compaction.

## 7. Date index (IndexKind 2)

Secondary index for time-range queries. Key is the composite `DateTicks (8) ‖ BlockId (16)` — the BlockId suffix makes keys unique so duplicate timestamps need no overflow handling; the value is empty. Range scan = seek to `(fromTicks, 0)`, iterate until `(toTicks, max)`. Root is registered in the Checkpoint's `SecondaryIndexes` table (IndexKind 2).

## 8. Caching

- Internal nodes: LRU by BlockId, verified on load (Section 5). At 10M emails the primary index's non-leaf levels total ~10 MB — cache them all.
- BlockLocationIndex upper levels: ~10 MB at 20M blocks — cache them all; leaf reads are the only per-lookup I/O.
- Leaves: optional small LRU; folder-page workloads rarely revisit leaves.
