---
acceptance_criteria:
- Regeneration reads only Tier 2 blocks never Tier 3
- Rebuilt pages match originals except flags reset to defaults
- Regenerated directory carries a bumped FolderVersion
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-16
id: US-EMDB-83
points: 3
priority: should
status: done
tags:
- v3
- folders
- recovery
title: Tier-2 page regeneration
updated: '2026-07-09'
---

As an operator, I want folder pages rebuildable from EmailMetadata alone (docs/Folder_Listing.md Section 4) so that lost or corrupt Tier 1 structures are a recovery event, not data loss.