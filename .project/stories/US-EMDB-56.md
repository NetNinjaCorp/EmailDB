---
acceptance_criteria:
- ChangePassword method derives old KEK from old password and existing salt
- Decrypts key store with old KEK
- Generates new salt and derives new KEK from new password
- Re-encrypts key store with new KEK
- Updates EncryptionHeader with new salt and key verification token
- Zero data blocks are read or written during password change
- All data remains readable with new password after change
- Old password no longer works after change
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-56
points: 3
priority: must
status: done
tags: []
title: Implement password change via KEK re-wrap
updated: '2026-02-24'
---

As a user, I want to change my database password without re-encrypting any data blocks, so that password changes are instant regardless of database size.