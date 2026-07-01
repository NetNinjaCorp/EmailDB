---
assignee: claude
created: '2026-02-24'
id: US-EMDB-43-4
points: 2
status: done
story_id: US-EMDB-43
title: 'Test: All typed methods encrypt/decrypt correctly with multi-epoch DEKs'
updated: '2026-02-25'
---

Verify all typed read/write methods (email content, folders, WAL, etc.) correctly handle encryption and decryption across blocks encrypted with different key epochs.