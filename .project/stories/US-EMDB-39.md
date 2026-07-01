---
acceptance_criteria:
- Encrypts payload to format Nonce(12) + Ciphertext(N) + AuthTag(16)
- Nonce = BlockId(8 bytes) + Random(4 bytes)
- Decrypts and verifies auth tag
- Uses System.Security.Cryptography.AesGcm
- Round-trip encrypt/decrypt produces identical plaintext
- Wrong key fails with clear error
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-39
points: 5
priority: must
status: done
tags: []
title: Implement AesGcmBlockEncryptionProvider
updated: '2026-02-24'
---

As a developer, I want an AES-GCM based encryption provider so that block payloads are encrypted with authenticated encryption providing both confidentiality and integrity.