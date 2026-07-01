---
acceptance_criteria:
- WAL blocks encrypted with active DEK and key epoch stamped in Flags
- Write methods encrypt WAL content
- RecoverFromDisk reads key epoch from each WAL block and decrypts with correct DEK
- WAL recovery works correctly with encrypted WAL blocks across multiple key epochs
- WAL blocks written before and after key rotation are both recoverable
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-45
points: 3
priority: should
status: done
tags: []
title: Integrate encryption into BTreeWALManager
updated: '2026-02-25'
---

Integrate the KeyWrappingEncryptionProvider (US-EMDB-55) into BTreeWALManager. WAL blocks should be encrypted with the active DEK and key epoch stamped in Flags. Recovery must read key epoch from each WAL block and decrypt with the correct DEK. Depends on US-EMDB-53 (Key Store), US-EMDB-54 (Flags update), US-EMDB-55 (KeyWrappingEncryptionProvider).