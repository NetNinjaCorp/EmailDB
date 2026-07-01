---
assignee: claude
created: '2026-02-24'
id: US-EMDB-46-5
points: 2
status: done
story_id: US-EMDB-46
title: 'Test: Retired DEKs with no remaining block references pruned from key store'
updated: '2026-02-25'
---

Verify that after compaction with re-encrypt, DEKs that no longer have any blocks referencing them are removed from the key store.