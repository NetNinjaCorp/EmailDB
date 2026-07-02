---
assignee: null
created: '2026-07-02'
depends_on:
- US-EMDB-64-7
id: US-EMDB-64-8
points: 3
status: todo
story_id: US-EMDB-64
tags: []
title: Implement v3 header/footer serialization and checksums
updated: '2026-07-02'
---

48-byte header (magic, version, type, flags, encoding, compression, 2-byte KeyEpoch, ULID, payload length, reserved), BLAKE3-128 header/payload checksums, 16-byte footer with ~magic and TotalBlockLength.