---
assignee: claude
created: '2026-02-24'
id: US-EMDB-46-3
points: 1
status: done
story_id: US-EMDB-46
title: 'Test: After re-encryption all blocks have current active epoch in Flags'
updated: '2026-02-25'
---

Verify that after compaction with re-encrypt, every encrypted block's Flags bits 1-7 match the current active epoch.