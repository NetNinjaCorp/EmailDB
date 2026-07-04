---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-67-5
id: US-EMDB-70-5
points: 2
status: todo
story_id: US-EMDB-70
tags: []
title: Implement IndexRoot serialization
updated: '2026-07-04'
---

68-byte payload: IndexKind(2) + RootBlockId(16) + EntryCount(8) + TreeHeight(2) + RootHash(32) + Sequence(8); monotonic Sequence per index.