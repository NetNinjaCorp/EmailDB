---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-73-5
points: 2
status: todo
story_id: US-EMDB-73
tags: []
title: Implement WAL block payload and writer
updated: '2026-07-02'
---

WalSequence + CheckpointBlockId + entries (Op, Key, BlockId, aux); writer stamps the current checkpoint's BlockId; retires the v1 raw fixed-region WAL.