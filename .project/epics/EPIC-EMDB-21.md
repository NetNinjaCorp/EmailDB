---
created: '2026-07-01'
id: EPIC-EMDB-21
points: null
priority: could
status: draft
tags:
- v3
- sync
- replication
target_date: null
title: Sync (Active-to-Backup)
updated: '2026-07-01'
---

One-way replication per docs/Sync.md: ULID high-water-mark transfer for immutable EmailContent/EmailMetadata, FolderVersion-based folder page replication, CheckpointSequence restore points, and the KeyStore-before-new-epoch ordering protocol (open design item, ExpertReport #16). Indexes and delta logs are derived per replica, never synced. Success: a backup replica converges after arbitrary interruption and can serve reads from its own rebuilt indexes.