---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-77-5
points: 3
status: done
story_id: US-EMDB-77
tags: []
title: Implement AES-GCM encrypt/decrypt with random nonces and AAD
updated: '2026-07-05'
---

12-byte CSPRNG nonces; AAD = FileId|BlockId|BlockType|KeyEpoch (35 bytes) required on both operations; Nonce|Ciphertext|Tag layout.