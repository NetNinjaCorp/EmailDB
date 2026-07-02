---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-84-6
id: US-EMDB-84-7
points: 2
status: todo
story_id: US-EMDB-84
tags: []
title: Implement Close
updated: '2026-07-02'
---

Flush index buffers, compile pending deltas if warranted, final Checkpoint, CleanShutdown=1 superblock write, release writer lock.