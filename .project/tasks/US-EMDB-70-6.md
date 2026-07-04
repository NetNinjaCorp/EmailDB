---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-70-5
- US-EMDB-68-6
id: US-EMDB-70-6
points: 3
status: todo
story_id: US-EMDB-70
tags: []
title: Implement WAL-buffered batch flush
updated: '2026-07-04'
---

In-memory buffer with count/time/explicit triggers; sort-by-key then single COW apply pass; failed flush leaves the previous root authoritative with orphaned nodes left for compaction.