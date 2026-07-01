---
created: '2026-02-22'
id: EPIC-EMDB-3
points: null
priority: must
status: archived
tags:
- zonetree
- indexing
target_date: null
title: ZoneTree Integration
updated: '2026-02-23'
---

Uncomment and complete the ZoneTree integration layer that bridges the BlockManager storage to ZoneTree's pluggable storage interface (IRandomAccessDeviceManager, IFileStreamProvider, IWriteAheadLogProvider). This enables email content KV storage and full-text search indexing within the EMDB file format. All ZoneTree adapter files are currently commented out.