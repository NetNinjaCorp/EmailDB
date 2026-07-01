---
assignee: claude
created: '2026-02-24'
id: US-EMDB-45-3
points: 1
status: done
story_id: US-EMDB-45
title: 'Test: RecoverFromDisk reads key epoch from each WAL block and decrypts with
  correct DEK'
updated: '2026-02-25'
---

Verify RecoverFromDisk reads the key epoch from each WAL block's Flags and uses KeyWrappingEncryptionProvider to look up the correct DEK for decryption.