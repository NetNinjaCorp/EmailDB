---
assignee: claude
created: '2026-02-24'
id: US-EMDB-46-2
points: 2
status: done
story_id: US-EMDB-46
title: 'Test: When re-encrypt enabled blocks decrypted with original DEK and re-encrypted
  with active DEK'
updated: '2026-02-25'
---

Verify that when re-encrypt is true, each block is decrypted using its key epoch's DEK and re-encrypted with the current active DEK. Blocks from multiple epochs should all end up with the active epoch.