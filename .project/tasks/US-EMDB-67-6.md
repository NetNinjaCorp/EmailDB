---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-67-5
id: US-EMDB-67-6
points: 2
status: done
story_id: US-EMDB-67
tags: []
title: Implement bounds-checked deserializers and capacity constants
updated: '2026-07-04'
---

EntryCount/KeySize/ValueSize validated against actual payload length before any read; capacity helpers matching spec Section 6.1 (83/50, 124/71, 166/55).