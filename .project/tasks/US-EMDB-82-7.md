---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-82-6
id: US-EMDB-82-7
points: 2
status: done
story_id: US-EMDB-82
tags: []
title: Implement listing merge of pending deltas
updated: '2026-07-09'
---

Read path merges the delta chain over page records in memory (adds inserted by date, deletes masked, flags overridden).