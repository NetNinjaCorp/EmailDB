---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-92-4
id: US-EMDB-97-5
points: 3
status: done
story_id: US-EMDB-97
tags: []
title: Implement bloom filter build, query, and registration
updated: '2026-07-11'
---

Per-folder filter over Tier 1 tokens sized for ~1% FP; rebuilt at page compile; consulted before multi-folder scans; always encrypted; Checkpoint registration (IndexKind 4).