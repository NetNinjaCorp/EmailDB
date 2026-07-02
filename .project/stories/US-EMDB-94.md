---
acceptance_criteria:
- Address-shaped queries route to the trigram index
- Date-bounded queries pre-filter via the date index
- Results merge and dedupe across phases with stable ordering
- Planner degrades gracefully when an index is absent
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-19
id: US-EMDB-94
points: 5
priority: could
status: backlog
tags:
- v3
- search
- planner
title: Query planner
updated: '2026-07-02'
---

As a user, I want one Search() entry point that routes across phases per the docs/Search.md planning matrix (address-shaped to FTS, date-bounded to date index, folder keyword to scan, and merges results) returning ranked EmailHashedIDs.