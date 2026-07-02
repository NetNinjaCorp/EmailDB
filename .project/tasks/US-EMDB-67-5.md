---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-67-5
points: 3
status: todo
story_id: US-EMDB-67
tags: []
title: Implement generic node models and serializers
updated: '2026-07-02'
---

12-byte node header (NodeKind, NodeVersion, IndexKind, KeySize, ValueSize, EntryCount, reserved); leaf and internal body layouts; NodeContentHash over the full serialized payload.