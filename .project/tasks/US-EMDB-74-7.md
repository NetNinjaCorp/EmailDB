---
assignee: claude
created: '2026-07-02'
depends_on:
- US-EMDB-74-6
id: US-EMDB-74-7
points: 3
status: done
story_id: US-EMDB-74
tags: []
title: Implement dirty-open scan and heal
updated: '2026-07-04'
---

Forward scan from the hinted Checkpoint for newer Checkpoints and matching WAL; replay; write fresh Checkpoint; bounded by post-checkpoint bytes.