---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-80-6
- US-EMDB-82-8
id: US-EMDB-83-4
points: 2
status: done
story_id: US-EMDB-83
tags: []
title: Implement Tier-2 regeneration routine
updated: '2026-07-09'
---

Enumerate folder membership, read EmailMetadata blocks only, rebuild listing records (flags to defaults), repack pages, write fresh directory with bumped FolderVersion.