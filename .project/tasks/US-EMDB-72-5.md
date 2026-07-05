---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-72-5
points: 2
status: done
story_id: US-EMDB-72
tags: []
title: Implement Checkpoint payload serialization
updated: '2026-07-04'
---

Root table (ULID + offset pairs), LocationIndexRoot field, SecondaryIndexes table {IndexKind, BlockId, Offset}, live/dead byte counters, FileId, CheckpointSequence, previous-checkpoint chain.