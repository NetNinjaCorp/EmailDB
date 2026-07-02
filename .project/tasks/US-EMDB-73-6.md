---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-73-5
id: US-EMDB-73-6
points: 3
status: todo
story_id: US-EMDB-73
tags: []
title: Implement WAL replay
updated: '2026-07-02'
---

Recovery scan collects WAL blocks matching the last valid Checkpoint, replays into index buffers and folder deltas, writes a fresh Checkpoint.