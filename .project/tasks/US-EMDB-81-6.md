---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-80-6
id: US-EMDB-81-6
points: 3
status: done
story_id: US-EMDB-81
tags: []
title: Implement listing record packing and FolderPage
updated: '2026-07-05'
---

~400 B packed record (EmailHashedID, BlockId, date, flags, size, From, Subject, Preview); ~80-record date-descending page payload.