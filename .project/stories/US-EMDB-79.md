---
acceptance_criteria:
- Password change writes one KeyStore block and one superblock update with zero data
  blocks touched
- Crash before the superblock write leaves the old password fully working
- Old password rejected and new password accepted after completion
- Rotation adds epoch N+1 and old-epoch blocks remain readable
- Rotation fails cleanly at epoch 65535 instead of wrapping
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-15
id: US-EMDB-79
points: 5
priority: must
status: done
tags:
- v3
- encryption
- rotation
title: Password change and key rotation
updated: '2026-07-05'
---

As a user, I want O(1) password change and key rotation (docs/Encryption.md Section 5) so that credential changes never rewrite data: password change touches only KeyStore + superblock and is crash-safe via dual slots; rotation appends a new DEK epoch.