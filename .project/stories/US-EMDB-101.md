---
acceptance_criteria:
- All listed files and code paths removed
- Solution builds with zero warnings about missing references
- No remaining reference to ZoneTree or OverrideLocation or int64 BlockId generation
  in EmailDB.Format
- git grep confirms no dead namespaces remain
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-22
id: US-EMDB-101
points: 5
priority: must
status: backlog
tags:
- v3
- cleanup
title: Delete v1 code paths
updated: '2026-07-02'
---

As a maintainer, I want the ADR-016 retirement list executed: ZonetreeSegmentIO.cs, commented-out EmailManager/MaintenanceManager, SegmentManager/FolderManager and Segment/Folder models, duplicate EmailDB.Format.Protobuf models + its MetadataManager, int64 BlockIdGenerator range partitioning, OverrideLocation and raw-region WAL paths, ZonetreeRef project.