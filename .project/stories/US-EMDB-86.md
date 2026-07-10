---
acceptance_criteria:
- GetEmail returns content for any committed email in O(log n) block reads
- Metadata-only fetch reads Tier 2 without touching Tier 3
- Unknown ID returns a clean not-found not an exception
- Read path verifies checksums and Merkle path per spec
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-17
id: US-EMDB-86
points: 5
priority: must
status: done
tags:
- v3
- api
- read-path
title: GetEmail and open-email read path
updated: '2026-07-10'
---

As a user, I want fast retrieval: primary index resolves EmailHashedID to BlockId, location index to physical offset, with Merkle-verified traversal; metadata-only fetch for opening an email without the body.