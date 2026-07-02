---
acceptance_criteria:
- Writes alternate slots with incremented sequence and fsync
- Open validates both slots and picks the higher valid sequence
- A torn slot is detected via checksum and repaired on next update
- Unknown IncompatFlags refuse open and unknown ReadOnlyCompatFlags force read-only
- CleanShutdown set to 0 on first write after open and 1 on graceful close
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-12
id: US-EMDB-63
points: 8
priority: must
status: backlog
tags:
- v3
- superblock
title: Superblock manager (dual-slot, feature flags, CleanShutdown)
updated: '2026-07-01'
---

As the storage engine, I want a dual-slot superblock so that file bootstrap state survives torn writes and the file is self-describing. Implements spec Section 3: 4096-byte slots at offsets 0/4096, alternating writes with monotonic sequence, BLAKE3-128 slot checksum, feature flag masks, CleanShutdown protocol, MaxPayloadLength, FileId/ShardIndex, encryption bootstrap fields, LastCheckpoint hint.