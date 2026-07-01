---
acceptance_criteria:
- Folder listing reads only metadata blocks (zero content block reads verified)
- GetEmailMetadataAsync returns EmailMetadataContent from BTree lookup
- GetEmailContentAsync follows ContentBlockId pointer to read content block
- Content block is only read when explicitly requested
- Metadata blocks are cached by CacheManager after first read
- Listing 1000 emails reads at most 1000 metadata blocks plus BTree traversal blocks
created: '2026-02-25'
epic_id: EPIC-EMDB-11
id: US-EMDB-60
points: 5
priority: must
status: backlog
tags: []
title: Implement two-tier email read flow (metadata-only listing + lazy content load)
updated: '2026-02-25'
---

As a developer, I want to read email metadata without loading content so that folder listings are fast and don't touch heavy content blocks.

**Read flows:**

1. **Folder listing (metadata-only):**
   - Load FolderContent → get List of EmailHashedIDs
   - BTree lookup each → get metadata block locations
   - Read metadata blocks (small, cacheable) → return subject, from, to, date, flags
   - Content blocks are NEVER read during listing

2. **Open email (full content):**
   - Already have EmailMetadataContent from listing step
   - Follow ContentBlockId → RawBlockManager.ReadBlockAsync(contentBlockId)
   - Return full email payload

3. **Single email lookup:**
   - BTree lookup EmailHashedID → metadata block location
   - Read metadata block
   - Optionally follow content pointer if full content needed

**Caching:** Metadata blocks should be cached aggressively by CacheManager since they're small and frequently accessed during folder browsing.