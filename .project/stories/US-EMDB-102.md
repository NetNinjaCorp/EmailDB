---
acceptance_criteria:
- v1-format tests removed or rewritten against v3
- Crypto-primitive tests retained and passing
- Full suite green in CI on Linux
- Coverage exists for every v3 spec section with a MUST
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-22
id: US-EMDB-102
points: 8
priority: must
status: backlog
tags:
- v3
- testing
- migration
title: Test suite migration to v3
updated: '2026-07-02'
---

As a maintainer, I want the ~140 unit tests rebased onto the v3 stack: keep still-valid crypto-primitive tests (Argon2, AES-GCM round-trip, BLAKE3), delete v1-format tests (old header sizes, PrevChainHash, epoch-in-flags, offset-addressed BTree), and ensure every v3 epic's acceptance criteria have coverage.