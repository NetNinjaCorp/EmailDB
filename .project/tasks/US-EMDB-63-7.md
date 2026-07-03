---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-63-6
id: US-EMDB-63-7
points: 2
status: done
story_id: US-EMDB-63
tags: []
title: Implement dual-slot write protocol
updated: '2026-07-03'
---

Alternate-slot writes with SuperblockSequence increment and fsync per write; pick-higher-valid-sequence on read; rewrite of an invalid slot on next update.