---
acceptance_criteria:
- Only folders with changed FolderVersion transfer
- Backup folder listings match the active after sync
- Compaction on the active side causes zero folder retransfer
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-21
id: US-EMDB-99
points: 5
priority: could
status: backlog
tags:
- v3
- sync
- folders
title: Folder replication via FolderVersion
updated: '2026-07-02'
---

As an operator, I want folder state replicated by comparing FolderVersion counters and transferring changed folders' directory + pages + delta chain wholesale (docs/Sync.md).