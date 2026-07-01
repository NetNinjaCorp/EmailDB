---
acceptance_criteria:
- Decision documented in architecture decisions doc
- 'If keeping: fix compilation errors and add .capnp schemas'
- 'If removing: delete project and clean solution references'
- No broken code left in the repository
created: '2026-02-22'
epic_id: EPIC-EMDB-2
id: US-EMDB-6
points: 3
priority: could
status: done
tags: []
title: Decide Cap'n Proto layer future and clean up
updated: '2026-02-23'
---

As a developer, I want a clear decision on whether Cap'n Proto stays or goes so that the codebase isn't carrying dead weight.

EmailDB.Format.CapnProto has partial implementations with compilation errors (undefined variables in BlockManager, broken CacheManager references). No .capnp schema files exist. Either fix and complete it as an alternate serialization option, or remove it and keep Protobuf only.