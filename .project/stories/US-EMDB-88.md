---
acceptance_criteria:
- Every COW rewrite and delete moves the superseded block's bytes to the dead counter
- Counters persist in each Checkpoint and survive reopen
- Trigger fires at Dead greater than Live without scanning
- Cleanup blocks record superseded BlockIds for audit
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-18
id: US-EMDB-88
points: 5
priority: should
status: backlog
tags:
- v3
- compaction
- accounting
title: Dead-block accounting and compaction triggers
updated: '2026-07-02'
---

As the maintenance layer, I want incremental live/dead byte accounting (docs/Compaction.md Section 3): bytes move live-to-dead at supersession/delete, Cleanup blocks (type 3) record supersession for audit, Checkpoint carries the counters, and triggers evaluate without any scan.