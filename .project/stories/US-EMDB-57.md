---
acceptance_criteria:
- RotateKey method generates new 32-byte random DEK
- New DEK assigned next epoch number
- Previous active epoch marked as retained not retired
- New epoch set as active in key store
- Key store block re-encrypted and written
- Future blocks use new DEK via active epoch
- Existing blocks remain readable with their original DEK
- Old and new epoch blocks coexist and are both decryptable
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-57
points: 3
priority: must
status: done
tags: []
title: Implement key rotation without re-encryption
updated: '2026-02-25'
---

As a security-conscious user, I want to rotate encryption keys periodically so that if a key is compromised, only blocks encrypted with that specific key epoch are exposed, limiting the blast radius.