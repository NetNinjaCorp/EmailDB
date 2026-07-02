---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-71-6
points: 3
status: todo
story_id: US-EMDB-71
tags: []
title: Implement offset-addressed location tree variant
updated: '2026-07-02'
---

IndexKind 1 tree over the generic node engine with ChildOffset internal records (16B key -> 8B offset + 8B length values); the one structure allowed raw offsets.