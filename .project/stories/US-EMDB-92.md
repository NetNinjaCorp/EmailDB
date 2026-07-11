---
acceptance_criteria:
- Folder-scoped scan of a 50K-email folder completes in ~15ms warm
- Matches include pending delta entries
- Whole-mailbox fallback works when no better phase applies
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-19
id: US-EMDB-92
points: 3
priority: should
status: done
tags:
- v3
- search
- scan
title: Listing page scan search
updated: '2026-07-11'
---

As a user, I want folder-scoped keyword search over Tier 1 Subject/From/Preview fields (docs/Search.md Phase 2) as the zero-index baseline and accuracy backstop for other phases.