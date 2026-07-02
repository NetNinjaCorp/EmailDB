---
assignee: null
created: '2026-07-02'
depends_on: []
id: US-EMDB-79-7
points: 2
status: todo
story_id: US-EMDB-79
tags: []
title: Implement RotateKey with epoch bound
updated: '2026-07-02'
---

Fresh DEK at ActiveEpoch+1, previous epoch retained; persisted KeyStore + superblock pointer update; clean failure at epoch 65535.