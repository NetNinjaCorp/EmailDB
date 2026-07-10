---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-89-6
id: US-EMDB-90-5
points: 3
status: done
story_id: US-EMDB-90
tags: []
title: Implement reEncrypt path in compaction copy
updated: '2026-07-10'
---

Decrypt with original-epoch DEK, re-encrypt at active epoch with fresh random nonce and AAD recomputed for the new epoch (BlockId unchanged), during the side-file copy pass.