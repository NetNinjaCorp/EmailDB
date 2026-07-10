---
acceptance_criteria:
- Listing one page of a 50K-email folder costs at most 3 block reads
- Date-jump uses binary search over directory date ranges
- FolderVersion increments on every directory rewrite
- Pages and directory encrypted under Default policy
- Listing record packs EmailHashedID BlockId date flags size from subject preview
  in ~400 bytes
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-16
id: US-EMDB-81
points: 8
priority: must
status: done
tags:
- v3
- folders
- pages
title: FolderPage and FolderPageDirectory
updated: '2026-07-09'
---

As a user, I want folder listings served from packed pages (docs/Folder_Listing.md Section 2): FolderPage (type 12) with ~80 date-descending listing records, FolderPageDirectory (type 11) with date-ranged page entries, FolderVersion counter, and delta head pointer.