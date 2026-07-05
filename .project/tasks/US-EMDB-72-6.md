---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-72-5
id: US-EMDB-72-6
points: 3
status: done
story_id: US-EMDB-72
tags: []
title: Implement checkpoint write protocol and reader
updated: '2026-07-04'
---

Write ordering per spec 10.3 (contents -> fsync -> Checkpoint -> fsync); reader verifies FileId and offset hints by BlockId match with silent re-resolution on mismatch.