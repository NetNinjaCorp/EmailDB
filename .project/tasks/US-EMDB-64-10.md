---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-64-9
id: US-EMDB-64-10
points: 2
status: done
story_id: US-EMDB-64
tags: []
title: Implement compression pipeline
updated: '2026-07-03'
---

Compression byte dispatch (None/LZ4/Zstd initially), applied after serialization before encryption, decompression bomb guard at MaxPayloadLength x16.