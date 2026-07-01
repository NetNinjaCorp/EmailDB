---
acceptance_criteria:
- CompactAsync accepts optional re-encrypt flag (default false)
- When re-encrypt enabled all blocks decrypted with their original DEK and re-encrypted
  with active DEK
- After re-encryption all blocks have the current active epoch in Flags
- Retired DEKs with no remaining block references pruned from key store
- New file gets updated key store with only active DEKs
- All data readable after compaction with or without re-encryption
- Compaction without re-encrypt flag works as normal space reclamation
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-46
points: 3
priority: should
status: done
tags: []
title: Optional re-encryption during compaction with DEK pruning
updated: '2026-02-25'
---

During compaction (space reclamation), optionally re-encrypt old blocks with the current active DEK and prune retired DEKs that no longer have any block references. This is an optimization, NOT a requirement for key rotation or password change (those are handled by US-EMDB-56 and US-EMDB-57 respectively). Re-encryption during compaction enables periodic key cleanup to limit the number of DEKs in the key store. After compaction with re-encryption, all blocks use the active epoch and old DEKs can be safely removed. Depends on US-EMDB-53, US-EMDB-55, US-EMDB-56, US-EMDB-57.