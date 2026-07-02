---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-89-7
id: US-EMDB-89-8
points: 2
status: todo
story_id: US-EMDB-89
tags: []
title: Implement atomic swap and leftover cleanup
updated: '2026-07-02'
---

fsync new file -> atomic rename (ReplaceFile semantics on Windows) -> directory fsync; delete stale .compact on open.