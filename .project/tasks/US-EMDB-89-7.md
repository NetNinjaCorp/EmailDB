---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-89-6
id: US-EMDB-89-7
points: 2
status: done
story_id: US-EMDB-89
tags: []
title: Implement location index rebuild during copy
updated: '2026-07-10'
---

Emit location entries in write order during the copy pass; bulk-load the new BlockLocationIndex bottom-up; fresh IndexRoots + Checkpoint.