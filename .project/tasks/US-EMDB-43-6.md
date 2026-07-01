---
assignee: claude
created: '2026-02-24'
id: US-EMDB-43-6
points: 2
status: done
story_id: US-EMDB-43
title: 'Test: Blocks written with different key epochs are all readable'
updated: '2026-02-25'
---

Write blocks using different key epochs (simulate key rotation), then verify all blocks are correctly decryptable by CacheManager using the KeyWrappingEncryptionProvider's DEK lookup.