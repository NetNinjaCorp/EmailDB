---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-90-5
id: US-EMDB-90-6
points: 2
status: done
story_id: US-EMDB-90
tags: []
title: Implement DEK pruning
updated: '2026-07-10'
---

After re-encryption, identify epochs with zero remaining block references and prune their DEKs from the KeyStore in the new file.