---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-85-5
id: US-EMDB-85-6
points: 3
status: todo
story_id: US-EMDB-85
tags: []
title: Implement group-commit batching
updated: '2026-07-02'
---

Multiple AddEmail calls share one flush + Checkpoint; explicit Commit() and auto-batch thresholds; bulk-add path.