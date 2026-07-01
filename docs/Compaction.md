# Compaction (v3)

How dead space is reclaimed in an append-only file. Normative protocol in the [File Format Spec](../EmailDB_FileFormat_Spec.md) Sections 10–11; this doc covers policy and rationale.

## 1. What creates dead blocks

Copy-on-write makes garbage continuously:

| Source | Rate |
|--------|------|
| BTree node rewrites (all indexes incl. BlockLocationIndex) | ~log(n) nodes per flush batch |
| FolderPage/Directory rewrites on delta compile | ~7 pages + 1 directory per 500 folder ops |
| Superseded Metadata / KeyStore / IndexRoot / Checkpoint / WAL versions | Per change |
| Deleted emails' content + metadata blocks | Per delete |
| FolderDeltaLog chains after compile | Per compile |

## 2. Two levels, honestly

An append-only single file cannot reclaim interior space in place. v3 therefore has exactly two mechanisms — not v2's three-tier scheme (its "L1 BTree-only compaction" reclaimed nothing without a file rewrite, and its in-place delta region is gone):

**Level 0 — inline reorganization (continuous, not really compaction).** Delta-log compilation into folder pages, WAL clearance at checkpoint, epoch consolidation opportunities. These *convert* live-but-fragmented state into compact state; the byproduct is dead blocks, counted below.

**Level 1 — full-file compaction (the only space reclaimer).** Side-file rewrite + atomic swap, spec Section 11.2:

1. Create `<name>.emdb.compact`: fresh superblocks (same FileId; `SuperblockSequence` continues)
2. Walk live roots from the latest Checkpoint; copy every reachable block
3. Rebuild the BlockLocationIndex from scratch for the new physical layout
4. Write fresh IndexRoots + Checkpoint; fsync file; atomic rename over the original; fsync directory

Crash-safe at every point: the old complete file or the new complete file, never a hybrid. Readers holding the old file finish on their snapshot (old inode / pending delete). **No logical block content is rewritten** — every persisted pointer is a ULID, so blocks just move; only the (derived) location index is rebuilt.

## 3. Triggers — no scanning to decide

Every Checkpoint records `LiveByteCount` / `DeadByteCount`, maintained incrementally (a block's bytes move from live to dead the moment a new version or a delete supersedes it; Cleanup blocks (type 3) record the supersession for audit).

| Trigger | Default |
|---------|---------|
| `DeadByteCount > 1.0 × LiveByteCount` (file ≈ 2× live data) | Compact on next idle window |
| `DeadByteCount > 3 × LiveByteCount` | Compact urgently |
| Retired DEK epochs pending pruning | Compact with `reEncrypt = true` when convenient |
| Manual | Always available |

Compaction is scheduled work, never on the write path.

## 4. Re-encryption option

With `reEncrypt = true`, copied payloads are decrypted and re-encrypted with the active DEK epoch (fresh random nonces; AAD recomputed with the unchanged BlockId and the new epoch). Afterwards, DEKs with zero remaining references are pruned from the KeyStore. This is the epoch-consolidation mechanism — optional, since compaction works fine copying ciphertext verbatim.

## 5. Interaction with sync

Compaction changes offsets but no BlockIds, versions, or `FolderVersion`s — a backup comparing ULID high-water marks and folder versions sees no difference. The new file's Checkpoint continues `CheckpointSequence`, so restore points remain ordered. The `.emdb.vec` sidecar is untouched (it references emails by ID, not offset).

## 6. Costs

Full compaction is O(live bytes) sequential read + write. A 50 GB shard at the 2× trigger carries ~25 GB live → roughly one sequential pass of each at disk speed. The rebuild of the BlockLocationIndex rides along in the same pass (entries emitted in write order, bulk-loaded bottom-up). Shards cap the worst case: compaction cost scales with shard size (~50 GB), not mailbox size.
