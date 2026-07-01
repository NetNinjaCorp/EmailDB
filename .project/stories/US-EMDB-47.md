---
acceptance_criteria:
- ADR-015 in DECISIONS.md documents KEK/DEK key-wrapping architecture
- EmailDB_FileFormat_Spec.md updated with key store block format and key epoch Flags
  layout
- ARCHITECTURE.md updated with key hierarchy diagram and encryption flow
- VISION.md updated to mention zero-cost password changes and forward secrecy via
  key rotation
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-47
points: 2
priority: should
status: backlog
tags: []
title: Encryption documentation and ADR
updated: '2026-02-24'
---

Document the KEK/DEK key-wrapping encryption architecture. ADR-015 has been written in DECISIONS.md. Remaining work: update the file format spec with key store block format, key epoch in Flags byte, and encryption flow diagrams. Update architecture and vision docs to reflect the two-tier key hierarchy.