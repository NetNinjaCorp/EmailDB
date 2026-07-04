---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-64-8
id: US-EMDB-64-9
points: 3
status: done
story_id: US-EMDB-64
tags: []
title: Implement append writer and verifying reader
updated: '2026-07-03'
---

Append-only block writer updating the runtime map; reader verifying header checksum first, PayloadLength <= MaxPayloadLength before allocation, payload checksum, then handing off to decrypt/decompress. Replaces v1 RawBlockManager read/write.