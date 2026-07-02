---
created: '2026-07-01'
id: EPIC-EMDB-14
points: null
priority: must
status: draft
tags:
- v3
- recovery
- checkpoint
- wal
- durability
target_date: null
title: Checkpoint, WAL &amp; Recovery
updated: '2026-07-01'
---

The commit and recovery machinery (spec Sections 7, 10, 13): BlockLocationIndex indirection table (BlockId to offset/length, offset-addressed internally, rebuilt by compaction), Checkpoint blocks as the commit point (root table + secondary index table + live/dead byte accounting), WAL as standard blocks with CheckpointBlockId replay fence, the open protocol (CleanShutdown fast path, bounded dirty-scan, O(log n) always), and the full corruption-handling contract with fault-injection coverage. Success: crash at any write boundary recovers to a consistent committed state; open never scans the whole file.