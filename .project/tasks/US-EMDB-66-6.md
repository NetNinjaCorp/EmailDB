---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-66-5
id: US-EMDB-66-6
points: 3
status: done
story_id: US-EMDB-66
tags: []
title: Implement forward scan with resynchronization
updated: '2026-07-04'
---

Sequential walk using header magic + TotalBlockLength; on corruption, hunt forward for the next valid header magic with valid checksum; log damaged offset ranges.