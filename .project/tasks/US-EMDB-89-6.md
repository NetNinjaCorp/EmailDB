---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-89-6
points: 3
status: done
story_id: US-EMDB-89
tags: []
title: Implement live-block walk and side-file copy
updated: '2026-07-10'
---

Walk live roots from the latest Checkpoint; copy reachable blocks to <name>.emdb.compact with fresh superblocks (same FileId, continued sequences).