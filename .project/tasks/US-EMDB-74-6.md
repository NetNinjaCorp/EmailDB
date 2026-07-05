---
assignee: claude
created: '2026-07-02'
depends_on: []
id: US-EMDB-74-6
points: 2
status: done
story_id: US-EMDB-74
tags: []
title: Implement clean-open fast path
updated: '2026-07-04'
---

Superblock -> (encryption bootstrap) -> hinted Checkpoint -> index roots; zero scanning when CleanShutdown=1.