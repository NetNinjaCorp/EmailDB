---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-81-6
id: US-EMDB-81-7
points: 3
status: done
story_id: US-EMDB-81
tags: []
title: Implement FolderPageDirectory
updated: '2026-07-05'
---

Directory payload with FolderId, FolderVersion, HeadDeltaBlockId, date-ranged PageEntries; binary search by date; COW rewrite bumping FolderVersion.