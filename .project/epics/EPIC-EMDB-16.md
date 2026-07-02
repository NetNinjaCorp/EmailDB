---
created: '2026-07-01'
id: EPIC-EMDB-16
points: null
priority: must
status: draft
tags:
- v3
- folders
- storage
- three-tier
target_date: null
title: Folder System &amp; Three-Tier Storage
updated: '2026-07-01'
---

Three-tier email model and folder pagination per docs/Folder_Listing.md: EmailContent (Tier 3) and EmailMetadata (Tier 2, with Preview field) blocks, FolderPageDirectory with FolderVersion and date-ranged page entries, packed FolderPage blocks (~80 emails), chained append-only FolderDeltaLog with compile-at-threshold, and Tier-2-only regeneration. Success: listing a page of a 50K-email folder costs 2-3 block reads; pages regenerate from Tier 2 without touching Tier 3.