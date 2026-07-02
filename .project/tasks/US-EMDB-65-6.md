---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-65-6
points: 2
status: todo
story_id: US-EMDB-65
tags: []
title: Implement fsync wrapper with fatal-poison semantics
updated: '2026-07-02'
---

Flush-to-disk (Flush(true)) wrapper; on fsync error, poison the handle, refuse further writes, force recovery on reopen. Never retry fsync.