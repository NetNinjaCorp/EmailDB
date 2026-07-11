---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-91-8
- US-EMDB-92-4
id: US-EMDB-94-5
points: 3
status: done
story_id: US-EMDB-94
tags: []
title: Implement query planner routing and merge
updated: '2026-07-11'
---

Classify queries (address-shaped, date-bounded, folder keyword, free text), route per docs/Search.md matrix, merge/dedupe results with stable ordering, graceful degradation when an index is absent.