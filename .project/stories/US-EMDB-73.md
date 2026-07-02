---
acceptance_criteria:
- WAL blocks after Checkpoint N carry N's BlockId
- Recovery replays only WAL matching the last valid Checkpoint then writes a fresh
  Checkpoint
- WAL referencing older checkpoints is treated as committed history
- WAL payload round-trips insert/delete/folder ops
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-14
id: US-EMDB-73
points: 5
priority: must
status: backlog
tags:
- v3
- wal
- recovery
title: WAL blocks with checkpoint replay fence
updated: '2026-07-01'
---

As the storage engine, I want WAL entries as standard append-only blocks carrying CheckpointBlockId (spec Section 10.4) so that recovery replays exactly the uncommitted operations and nothing else. Retires the v1 raw fixed-region WAL.