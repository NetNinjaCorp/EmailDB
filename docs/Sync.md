# Sync: Active-to-Backup Replication

EmailDB supports one-way replication from an Active (primary) instance to a Backup (read-only follower). Multi-master sync is out of scope.

## Email Content Sync

EmailContent blocks are immutable -- never overwritten, never re-encrypted. Sync reduces to "copy blocks the other side doesn't have."

The backup tracks a **ULID high-water mark**: the ULID of the last EmailContent block it received.

1. Backup asks Active: "give me all EmailContent blocks with ULID > X"
2. Active streams the delta blocks
3. Backup appends them to its own file and advances its high-water mark

Because ULIDs sort in creation order, delta computation is a single comparison.

## Folder Sync

Each folder has a `FolderVersion` counter (ulong) on its `FolderPageDirectory`. This counter increments every time the delta log compiles into folder pages.

1. Backup reports its `FolderVersion` per folder
2. For any folder where the backup is behind, Active sends current folder pages wholesale
3. Backup replaces its pages for that folder

If `FolderVersion` matches, no transfer is needed.

### What FolderVersion Replaces

There is no separate sync log, no tombstone block type, no BTree diffing, and no delta log retention for sync. The `FolderDeltaLog` is a **local-only** structure for folder page maintenance -- it is not a replication mechanism.

## KeyStore Sync

If the Active rotates encryption keys (new epoch), the updated KeyStore block must be sent **before** any EmailContent blocks encrypted with the new epoch. Otherwise the backup cannot decrypt them.

## Multi-Machine Writes

A second machine does not write directly to the storage file. Instead:

1. **Active1** is the single authoritative writer for all mutable structures (folders, BTree, flags)
2. **Active2** submits mutations (Add, Delete, Move, FlagChange) via an application-layer **actions channel** (RPC)
3. Active1 applies mutations locally; results replicate to Active2 through normal Active-to-Backup replication
4. Active2 is a client with a local read replica -- it never has divergent folder state

The actions channel is transport-agnostic (HTTP, gRPC, message queue) and is **not** part of the storage format spec.

This eliminates all merge conflicts: Active1 is the single writer for folder structure. EmailContent blocks are conflict-free because they are content-addressed (same `EmailHashedID` = idempotent insert).

## What Syncs vs. What Doesn't

| Data | Syncs? | Mechanism |
|------|--------|-----------|
| EmailContent blocks | Yes | ULID high-water mark |
| Folder pages | Yes | FolderVersion comparison, wholesale page transfer |
| KeyStore | Yes | Sent before new-epoch content blocks |
| BTree index | No | Each replica maintains its own index from its own block layout |
| FolderDeltaLog | No | Local-only, consumed on page compile |
| File offsets | No | Runtime-only, each replica builds its own `Dictionary<Ulid, long>` |
| Checkpoint | No | Local to each file |

## Offset Independence

BTree leaf entries store `BlockId` (ULID), not file offsets. Each replica maintains its own `Dictionary<Ulid, long>` mapping BlockId to physical offset. This cleanly separates durable identity (ULID, stable across replicas and compaction) from physical layout (offset, local to each file). Syncing never transfers or remaps offsets.
