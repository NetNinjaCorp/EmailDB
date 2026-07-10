---
acceptance_criteria:
- reEncrypt=true leaves every block at the active epoch with fresh nonces
- BlockIds unchanged and AAD recomputed with the new epoch
- DEKs with zero remaining references pruned from the KeyStore
- reEncrypt=false copies ciphertext verbatim and prunes nothing
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-18
id: US-EMDB-90
points: 5
priority: should
status: done
tags:
- v3
- compaction
- encryption
title: Compaction re-encryption and DEK pruning
updated: '2026-07-10'
---

As a security-conscious user, I want optional re-encryption during compaction (docs/Compaction.md Section 4): copied payloads re-encrypted at the active epoch with fresh nonces and recomputed AAD, then unreferenced DEKs pruned from the KeyStore.