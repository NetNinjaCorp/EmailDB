---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-88-5
points: 3
status: done
story_id: US-EMDB-88
tags: []
title: Implement live/dead byte accounting and Cleanup blocks
updated: '2026-07-10'
---

Counters updated at every supersession/delete, persisted in Checkpoints, restored on open; Cleanup blocks record superseded BlockIds.