---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-101-5
points: 3
status: todo
story_id: US-EMDB-101
tags: []
title: Delete v1 files and code paths
updated: '2026-07-02'
---

Remove per ADR-016: ZonetreeSegmentIO.cs, commented EmailManager/MaintenanceManager, SegmentManager/FolderManager + Segment/Folder models, EmailDB.Format.Protobuf duplicate Models + MetadataManager, int64 BlockIdGenerator range partitioning, OverrideLocation + raw-region WAL paths, ZonetreeRef project, ZoneTree csproj globs. Keep the build green after each removal group.