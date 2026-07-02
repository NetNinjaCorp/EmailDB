---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-64-7
points: 2
status: todo
story_id: US-EMDB-64
tags: []
title: Implement monotonic ULID generator
updated: '2026-07-02'
---

128-bit ULIDs: 48-bit ms timestamp + 80-bit CSPRNG randomness, big-endian 16-byte binary layout, monotonic-increment mode under same-millisecond and clock-regression conditions.