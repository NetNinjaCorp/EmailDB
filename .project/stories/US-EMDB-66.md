---
acceptance_criteria:
- Runtime map tracks all blocks appended this session
- Forward scan resynchronizes past a corrupt block by hunting the next valid header
  magic and logs the damaged range
- Duplicate BlockIds resolve last-position-wins
- Backward walk from EOF via footers finds the last valid block after a torn tail
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-12
id: US-EMDB-66
points: 5
priority: must
status: done
tags:
- v3
- recovery
- scan
title: Runtime block map and scan fallback
updated: '2026-07-04'
---

As the storage engine, I want a runtime ULID-to-offset map plus resilient scan routines so that blocks written since the last checkpoint resolve instantly and disaster recovery can rebuild state from raw bytes (spec Sections 7, 11, 13).