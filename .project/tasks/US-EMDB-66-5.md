---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-66-5
points: 1
status: done
story_id: US-EMDB-66
tags: []
title: Implement runtime ULID-to-offset map
updated: '2026-07-04'
---

Concurrent map of blocks appended this session (pre-checkpoint blocks); duplicate BlockId last-position-wins semantics.