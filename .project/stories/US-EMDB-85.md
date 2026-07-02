---
acceptance_criteria:
- Duplicate content is detected via EmailHashedID and not stored twice
- A committed AddEmail survives crash and recovery
- 1000-email bulk add commits in batches with bounded checkpoint count
- All indexes and the folder listing observe the email after commit
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-17
id: US-EMDB-85
points: 8
priority: must
status: backlog
tags:
- v3
- api
- write-path
title: AddEmail pipeline
updated: '2026-07-02'
---

As a user, I want AddEmail to persist an email end-to-end: EmailHashedID dedupe, Tier 3 + Tier 2 blocks, WAL entry, primary and date index inserts, folder delta append, group-commit batching to one Checkpoint.