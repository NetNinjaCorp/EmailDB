---
acceptance_criteria:
- HashedSearchEngine configured with EmailHashedID key type
- Email subject/from/to/body indexed on add
- SearchEmailsAsync returns matching EmailHashedIDs
- Search index persisted through ZoneTree storage adapters
- Delete/update operations maintain search index consistency
created: '2026-02-22'
epic_id: EPIC-EMDB-3
id: US-EMDB-8
points: 5
priority: should
status: archived
tags: []
title: Implement ZoneTree embedding-based vector search integration
updated: '2026-02-23'
---

ARCHIVED: ZoneTree-based search replaced by self-built B+-tree (EPIC-EMDB-8) + HNSW embedding search (EPIC-EMDB-7). HashedSearchEngine integration is no longer needed.