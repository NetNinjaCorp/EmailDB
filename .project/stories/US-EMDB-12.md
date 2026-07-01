---
acceptance_criteria:
- WAL entries written before destructive operations
- Recovery process detects incomplete operations on startup
- Incomplete operations are rolled back or completed
- WAL entries cleaned up after successful operations
- System recovers correctly after simulated crash
created: '2026-02-22'
epic_id: EPIC-EMDB-5
id: US-EMDB-12
points: 8
priority: should
status: backlog
tags: []
title: Implement WAL-based crash recovery
updated: '2026-02-22'
---

As a developer, I want write-ahead logging to enable crash recovery so that partially written operations can be rolled back or completed on restart.

WALContent model exists with entries by category and offset tracking. The WAL system needs to log operations before they execute and replay/rollback on recovery. MetadataManager already tracks WAL offsets. The LLMText.txt shows a previous BlockManager implementation that had journaling support.