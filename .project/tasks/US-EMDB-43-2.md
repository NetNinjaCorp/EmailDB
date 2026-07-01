---
assignee: claude
created: '2026-02-24'
id: US-EMDB-43-2
points: 2
status: done
story_id: US-EMDB-43
title: 'Test: WriteBlockAsync encrypts payload with active DEK and sets Flags bit
  0 plus key epoch'
updated: '2026-02-25'
---

Verify WriteBlockAsync encrypts the payload using the active DEK from the key store, sets bit 0 (encrypted), and stamps the active key epoch into bits 1-7 of the Flags byte.