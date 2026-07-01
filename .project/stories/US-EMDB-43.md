---
acceptance_criteria:
- CacheManager accepts optional IBlockEncryptionProvider
- WriteBlockAsync encrypts payload with active DEK and sets Flags bit 0 plus key epoch
  in bits 1-7
- ReadBlockAsync reads key epoch from Flags and decrypts with correct DEK
- All typed methods encrypt/decrypt correctly with multi-epoch DEKs
- Cache stores decrypted content objects not ciphertext
- Blocks written with different key epochs are all readable
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-43
points: 5
priority: must
status: done
tags: []
title: Integrate encryption into CacheManager read/write paths
updated: '2026-02-25'
---

Integrate the KeyWrappingEncryptionProvider (US-EMDB-55) into CacheManager's read/write paths. CacheManager should accept an optional IBlockEncryptionProvider (which will be the KeyWrappingEncryptionProvider in production). On write: encrypt payload using active DEK, stamp key epoch into block Flags, compute BLAKE3-128 checksum on ciphertext. On read: read key epoch from Flags, decrypt with correct DEK via KeyWrappingEncryptionProvider, cache stores decrypted content objects. Depends on US-EMDB-53 (Key Store), US-EMDB-54 (Flags update), US-EMDB-55 (KeyWrappingEncryptionProvider).