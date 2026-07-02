---
acceptance_criteria:
- Each contract row has a dedicated fault-injection test that asserts the required
  behavior
- Corruption and wrong-key and tamper surface as distinct error types
- Damaged byte ranges are logged with offsets
- Referenced live data loss surfaces the affected BlockId
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-14
id: US-EMDB-75
points: 8
priority: must
status: backlog
tags:
- v3
- corruption
- testing
title: Corruption-handling contract implementation
updated: '2026-07-01'
---

As the storage engine, I want every verification failure to behave exactly as spec Section 13 requires so that corruption handling is deterministic, not improvised. Covers superblock slots, header/payload checksums, insane lengths, GCM tag failures, Merkle mismatches, torn checkpoints, torn tails, stale offset hints, decompression bombs.