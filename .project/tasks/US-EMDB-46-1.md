---
assignee: claude
created: '2026-02-24'
id: US-EMDB-46-1
points: 1
status: done
story_id: US-EMDB-46
title: 'Test: CompactAsync accepts optional re-encrypt flag (default false)'
updated: '2026-02-25'
---

Verify CompactAsync has a re-encrypt parameter that defaults to false. When false, compaction does normal space reclamation without re-encryption.