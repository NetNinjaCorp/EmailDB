---
acceptance_criteria:
- Move appears in both folders' listings without rewriting content blocks
- Delete removes the email from listings and indexes and counts its bytes dead
- Flag change is visible in the next listing read
- List API returns a stable date-descending page for any offset within the folder
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-17
id: US-EMDB-87
points: 5
priority: must
status: done
tags:
- v3
- api
- operations
title: Move, delete, flag, and list operations
updated: '2026-07-10'
---

As a user, I want the remaining mailbox operations: move (two folder deltas), delete (membership removal, index entry removal, dead-byte accounting), flag changes, and a folder listing API returning page slices with pending deltas merged.