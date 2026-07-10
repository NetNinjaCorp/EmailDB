---
acceptance_criteria:
- Add/Delete/FlagChange entries append to the chain via PreviousDeltaBlockId
- Listing merges pending deltas with pages in memory
- Compile at ~500 pending entries rewrites only affected pages COW and resets the
  delta head
- Old pages directory versions and consumed delta chain become dead after compile
- Move is two delta entries with content blocks untouched
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-16
id: US-EMDB-82
points: 8
priority: must
status: done
tags:
- v3
- folders
- delta
title: FolderDeltaLog chain and compile
updated: '2026-07-09'
---

As the folder layer, I want folder mutations buffered in chained append-only delta blocks (type 13) and compiled into pages at threshold (docs/Folder_Listing.md Sections 2-3) so that adds are cheap and pages stay fresh without in-place writes.