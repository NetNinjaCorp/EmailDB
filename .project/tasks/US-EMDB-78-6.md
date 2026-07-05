---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-78-5
id: US-EMDB-78-6
points: 3
status: done
story_id: US-EMDB-78
tags: []
title: Wire decrypt and error taxonomy into read path
updated: '2026-07-05'
---

Read path: checksum -> tag order; corruption vs wrong-key vs tamper distinct errors; mixed-policy files handled per-block via the Encrypted flag.