---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-76-6
- US-EMDB-76-7
- US-EMDB-77-6
id: US-EMDB-76-8
points: 3
status: done
story_id: US-EMDB-76
tags: []
title: Implement bootstrap wiring
updated: '2026-07-05'
---

Open-time sequence: superblock fields -> KEK -> token check -> KeyStore block decrypt -> KeyWrapping provider construction -> injection into BlockStore. The v1 gap this closes: no production path ever built this chain.