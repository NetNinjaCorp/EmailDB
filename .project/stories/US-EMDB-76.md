---
acceptance_criteria:
- Password NFC-normalized then UTF-8 encoded before KDF
- KDF parameters read from superblock and honored even when they differ from defaults
- Wrong password fails fast via KeyVerificationToken with a distinct error
- Tampered Salt or KdfParams detected via token failure
- Provider decrypts blocks across multiple epochs from the loaded DEK table
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-15
id: US-EMDB-76
points: 8
priority: must
status: done
tags:
- v3
- encryption
- bootstrap
title: Encryption bootstrap (superblock to provider)
updated: '2026-07-05'
---

As a user, I want opening an encrypted file to derive keys entirely from the file plus my password (docs/Encryption.md Section 2): NFC normalization, Argon2id from stored KdfParams/Salt, KeyVerificationToken fast-fail, KeyStore decryption, provider construction from the DEK table. No compiled-in KDF constants anywhere.