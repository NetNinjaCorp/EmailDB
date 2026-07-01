---
acceptance_criteria:
- EncryptionHeader contains magic scheme version algorithm ID KDF type salt key verification
  token
- Key verification token = encrypt known plaintext EMDB with derived key
- Wrong key = immediate error
- EncryptionHeaderManager reads/writes header
- Unencrypted files have no EncryptionHeader
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-42
points: 3
priority: must
status: done
tags: []
title: Implement EncryptionHeader for file-level crypto metadata
updated: '2026-02-24'
---

As a developer, I want an encryption header stored in the database file so that the system can detect encryption, verify the key early, and store KDF parameters for key derivation.