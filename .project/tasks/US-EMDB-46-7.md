---
assignee: claude
created: '2026-02-24'
id: US-EMDB-46-7
points: 2
status: done
story_id: US-EMDB-46
title: 'Test: Compaction without re-encrypt flag works as normal space reclamation'
updated: '2026-02-25'
---

Verify that when re-encrypt is false (default), compaction does normal space reclamation without touching encryption - blocks retain their original key epochs and all DEKs remain in the key store.