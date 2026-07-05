---
acceptance_criteria:
- Nonces are 12 CSPRNG bytes and never derived from IDs or counters
- Encrypt and decrypt both require AAD and a ciphertext moved to another BlockId or
  BlockType or epoch or FileId fails authentication
- On-disk layout is Nonce then Ciphertext then Tag adding exactly 28 bytes
- Password KEK and DEK buffers zeroized when scope ends
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-15
id: US-EMDB-77
points: 5
priority: must
status: done
tags:
- v3
- encryption
- aes-gcm
title: AES-GCM v3 provider (random nonces, AAD, zeroization)
updated: '2026-07-05'
---

As the storage engine, I want the per-block encryption primitive per spec Section 9.3: AES-256-GCM, 12 fully random CSPRNG nonces, mandatory AAD (FileId|BlockId|BlockType|KeyEpoch), epoch-based DEK lookup on decrypt, and key-material zeroization.