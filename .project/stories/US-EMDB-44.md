---
acceptance_criteria:
- BTreeIndex accepts optional IBlockEncryptionProvider
- Write methods encrypt after serialization using active DEK and stamp key epoch
- Read methods read key epoch from Flags and decrypt before deserialization
- BLAKE3 hash chain computed on plaintext before encryption and verified after decryption
- B-tree nodes written with different key epochs after rotation are all readable
- All BTree operations work correctly with encryption enabled
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-44
points: 5
priority: must
status: done
tags: []
title: Integrate encryption into BTreeIndex read/write paths
updated: '2026-02-25'
---

Integrate the KeyWrappingEncryptionProvider (US-EMDB-55) into BTreeIndex read/write paths. B-tree node blocks should be encrypted after serialization and decrypted before deserialization. Key epoch stamped in Flags. BLAKE3 hash chain operates on plaintext (hash before encrypt, verify after decrypt) to maintain tamper detection. Depends on US-EMDB-53 (Key Store), US-EMDB-54 (Flags update), US-EMDB-55 (KeyWrappingEncryptionProvider).