---
assignee: claude
created: '2026-02-24'
id: US-EMDB-44-2
points: 2
status: done
story_id: US-EMDB-44
title: 'Test: Write methods encrypt after serialization using active DEK and stamp
  key epoch'
updated: '2026-02-25'
---

Verify B-tree write methods encrypt node payloads using the active DEK from KeyWrappingEncryptionProvider and stamp the active key epoch into bits 1-7 of the Flags byte.