---
assignee: claude
created: '2026-02-24'
id: US-EMDB-45-1
points: 2
status: done
story_id: US-EMDB-45
title: 'Test: WAL blocks encrypted with active DEK and key epoch stamped in Flags'
updated: '2026-02-25'
---

Verify WAL blocks are encrypted using the active DEK from the key store and the active key epoch is stamped into Flags bits 1-7.