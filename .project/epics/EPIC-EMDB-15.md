---
created: '2026-07-01'
id: EPIC-EMDB-15
points: null
priority: must
status: draft
tags:
- v3
- security
- encryption
target_date: null
title: Encryption v3
updated: '2026-07-01'
---

Wire the KEK/DEK encryption system into the v3 stack per spec Section 9 and docs/Encryption.md: superblock bootstrap (NFC password normalization, Argon2id from stored KdfParams, KeyVerificationToken fast-fail, KeyStore decrypt, provider construction), per-block AES-256-GCM with random 12-byte nonces and mandatory AAD (FileId|BlockId|BlockType|KeyEpoch), 2-byte epochs, policy-driven encryption on the write/read path, O(1) password change and key rotation, zeroization. The v1 crypto primitives exist but were never integrated - v3 makes encryption a first-class path, not standalone code. Success: encrypted file round-trips; wrong password, tampering, and corruption yield three distinct errors; transplant attacks fail.