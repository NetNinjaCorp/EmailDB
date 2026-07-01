---
acceptance_criteria:
- BlockType.KeyStore added to enum
- KeyStoreContent model holds epoch/DEK/timestamp/retired entries plus ActiveEpoch
- KeyStoreManager encrypts key store payload with KEK using AES-256-GCM
- KeyStoreManager decrypts key store with KEK and loads DEK table
- On file creation initial DEK epoch 0 is generated and key store block written
- On file open KEK derived from password decrypts key store and loads DEK table
- Key store round-trips correctly through serialize/encrypt/decrypt/deserialize
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-53
points: 5
priority: must
status: done
tags: []
title: Implement Key Store Block and KeyStoreManager
updated: '2026-02-24'
---

As a developer, I want a Key Store Block (BlockType.KeyStore) that holds a table of KeyEpoch-to-DEK mappings encrypted with the KEK, so that the system supports multi-key encryption with O(1) password changes and key rotation without re-encrypting data blocks.