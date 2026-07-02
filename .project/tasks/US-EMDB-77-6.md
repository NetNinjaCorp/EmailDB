---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-77-5
id: US-EMDB-77-6
points: 2
status: todo
story_id: US-EMDB-77
tags: []
title: Implement epoch DEK lookup and zeroization
updated: '2026-07-02'
---

Decrypt selects DEK by header KeyEpoch; missing/retired epoch errors; KEK/DEK buffers zeroized on disposal.