---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-75-5
id: US-EMDB-75-6
points: 3
status: done
story_id: US-EMDB-75
tags: []
title: Implement per-failure handlers
updated: '2026-07-05'
---

Wire each spec Section 13 row's required behavior into the read/open/recovery paths (resynchronize, fall back, refuse, log ranges).