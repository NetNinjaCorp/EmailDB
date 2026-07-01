---
assignee: claude
created: '2026-02-24'
id: US-EMDB-45-4
points: 1
status: done
story_id: US-EMDB-45
title: 'Test: WAL recovery works correctly with encrypted WAL blocks across multiple
  key epochs'
updated: '2026-02-25'
---

Verify WAL recovery correctly handles WAL blocks written before and after a key rotation — blocks from different key epochs should all be recoverable.