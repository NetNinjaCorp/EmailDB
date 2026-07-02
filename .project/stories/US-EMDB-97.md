---
acceptance_criteria:
- Filter sized for ~1% false positives over Tier 1 tokens
- Multi-folder search consults filters before scanning
- Filters always encrypted
- Filters rebuilt with page compile and registered in the Checkpoint
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-20
id: US-EMDB-97
points: 5
priority: could
status: backlog
tags:
- v3
- search
- bloom
title: Per-folder bloom filters
updated: '2026-07-02'
---

As the search layer, I want per-folder bloom filters (type 18, docs/Search.md Phase 5) so that multi-folder searches skip folders that cannot match before paying page scans.