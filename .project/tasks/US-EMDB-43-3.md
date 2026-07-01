---
assignee: claude
created: '2026-02-24'
id: US-EMDB-43-3
points: 2
status: done
story_id: US-EMDB-43
title: 'Test: ReadBlockAsync reads key epoch from Flags and decrypts with correct
  DEK'
updated: '2026-02-25'
---

Verify ReadBlockAsync reads the key epoch from bits 1-7 of the Flags byte and passes it to KeyWrappingEncryptionProvider to look up the correct DEK for decryption.