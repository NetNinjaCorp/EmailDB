---
acceptance_criteria:
- MaintenanceManager uncommented and compiles
- CompactAsync creates a new file with only latest block versions
- Cleanup removes blocks listed in OutdatedOffsets
- Metadata updated after compaction
- File replacement is atomic (no data loss on failure)
created: '2026-02-22'
epic_id: EPIC-EMDB-5
id: US-EMDB-11
points: 5
priority: should
status: backlog
tags: []
title: Implement MaintenanceManager compaction and cleanup
updated: '2026-02-22'
---

As a developer, I want MaintenanceManager to perform file compaction and cleanup so that the EMDB file doesn't grow unbounded with old block versions.

MaintenanceManager.cs is entirely commented out. Per the append-only architecture, old block versions accumulate over time. Compaction creates a new file with only the latest versions of active blocks. Cleanup removes outdated segments tracked in MetadataContent.OutdatedOffsets. The integration-patterns.md doc describes the compaction workflow.