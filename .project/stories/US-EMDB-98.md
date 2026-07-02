---
acceptance_criteria:
- Backup converges to the active file's content set after arbitrary interruption
- Delta computation is a single high-water-mark comparison
- Backup rebuilds its own indexes from received blocks
- Replicated blocks verify checksums and AAD on the backup side
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-21
id: US-EMDB-98
points: 8
priority: could
status: backlog
tags:
- v3
- sync
- content
title: Content replication via ULID high-water mark
updated: '2026-07-02'
---

As an operator, I want one-way replication of immutable EmailContent/EmailMetadata blocks to a backup (docs/Sync.md): delta computed by ULID comparison, resumable after interruption, CheckpointSequence restore points.