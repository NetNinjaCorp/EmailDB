---
acceptance_criteria:
- IBlockEncryptionProvider interface with Encrypt/Decrypt/ShouldEncrypt/OverheadBytes/IsEnabled
- NullBlockEncryptionProvider passes through all payloads unchanged
- Block.cs has FlagEncrypted constant and IsEncrypted property
- All existing tests pass unchanged
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-38
points: 3
priority: must
status: done
tags: []
title: Define IBlockEncryptionProvider interface and NullBlockEncryptionProvider
updated: '2026-02-24'
---

As a developer, I want a well-defined encryption provider interface so that encryption can be plugged into the block storage layer without coupling to a specific algorithm.