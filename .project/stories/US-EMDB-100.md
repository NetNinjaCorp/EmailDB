---
acceptance_criteria:
- Protocol documented in docs/Sync.md closing the open design item
- A backup never holds a content block whose epoch DEK it lacks
- Interrupted sync during epoch transition recovers correctly
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-21
id: US-EMDB-100
points: 3
priority: could
status: backlog
tags:
- v3
- sync
- keystore
title: KeyStore sync ordering protocol
updated: '2026-07-02'
---

As an operator, I want the KeyStore-before-new-epoch ordering defined and implemented (open item from ExpertReport #16) so that a backup can always decrypt what it has received: design the interleaving of KeyStore updates into the ULID-ordered content stream, then implement it.