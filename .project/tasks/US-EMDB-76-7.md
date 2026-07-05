---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-76-7
points: 2
status: done
story_id: US-EMDB-76
tags: []
title: Implement KeyVerificationToken create/verify
updated: '2026-07-05'
---

Nonce(12) + AES-GCM(KEK, "EMDB") + Tag(16) in the superblock; distinct wrong-password/tampered-header error on failure.