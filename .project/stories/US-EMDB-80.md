---
acceptance_criteria:
- EmailHashedID is SHA3-256 over canonical content and is stable across sessions
- EmailMetadata round-trips headers MIME structure threading refs and Preview
- Preview extracted as plain text from HTML or text bodies
- Both block types compress with Zstd and encrypt under Default policy
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-16
id: US-EMDB-80
points: 5
priority: must
status: done
tags:
- v3
- email
- tiers
title: Tier 2/3 email blocks (EmailMetadata + EmailContent)
updated: '2026-07-05'
---

As the email layer, I want Tier 2 and Tier 3 block models (docs/Folder_Listing.md Section 1): EmailContent (type 7) raw MIME, EmailMetadata (type 10) with full RFC 5322 headers, MIME structure, threading refs, and the Preview field (~200 chars) that makes Tier 1 regenerable; SHA3-256 EmailHashedID content identity.