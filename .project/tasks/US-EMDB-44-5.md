---
assignee: claude
created: '2026-02-24'
id: US-EMDB-44-5
points: 2
status: done
story_id: US-EMDB-44
title: 'Test: All BTree operations work correctly with encryption across key epochs'
updated: '2026-02-25'
---

Verify insert, lookup, delete, and range queries all work correctly when the tree contains nodes encrypted with different key epochs (simulating key rotation during tree growth).