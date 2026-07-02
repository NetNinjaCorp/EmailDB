---
acceptance_criteria:
- Entries for blocks since last checkpoint batch-insert at checkpoint time
- Internal nodes use ChildOffset not ChildBlockId
- Resolution precedence is runtime map then location index then full scan
- Index regenerates from a full scan and matches
- Lookup of any committed block is O(log n) with upper levels cached
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-14
id: US-EMDB-71
points: 8
priority: must
status: backlog
tags:
- v3
- location-index
- recovery
title: BlockLocationIndex (indirection table)
updated: '2026-07-01'
---

As the storage engine, I want a persistent BlockId-to-(Offset,Length) index (spec Section 7, IndexKind 1) so that ULID-only logical pointers resolve in O(log n) and open never scans the file. Offset-addressed internally (it cannot depend on itself); derived data rebuilt by compaction or full scan.