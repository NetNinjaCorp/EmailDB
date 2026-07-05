---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-76-8
id: US-EMDB-79-6
points: 3
status: done
story_id: US-EMDB-79
tags: []
title: Implement ChangePassword
updated: '2026-07-05'
---

Decrypt KeyStore with old KEK -> new Salt (+ optional KdfParams upgrade) -> new KEK -> append re-encrypted KeyStore -> superblock update. Crash-safe ordering; zero data blocks.