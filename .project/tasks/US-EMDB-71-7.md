---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-71-6
id: US-EMDB-71-7
points: 2
status: todo
story_id: US-EMDB-71
tags: []
title: Implement checkpoint-time batch insert
updated: '2026-07-02'
---

Collect runtime-map entries since last checkpoint, sort by BlockId, batch-insert COW, emit new location IndexRoot before the Checkpoint block.