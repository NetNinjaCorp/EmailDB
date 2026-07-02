---
acceptance_criteria:
- Default policy encrypts content/folders/WAL/FTS/bloom and leaves BTree/Metadata/Checkpoint
  plaintext
- Full policy encrypts everything except Metadata/Cleanup/Checkpoint/KeyStore-rules
  per spec
- Read verifies checksum before GCM tag and corruption vs wrong-key vs tamper are
  three distinct errors
- Mixed-policy files read correctly block-by-block via the Encrypted flag
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-15
id: US-EMDB-78
points: 5
priority: must
status: backlog
tags:
- v3
- encryption
- policy
title: Policy-driven encryption on the write/read path
updated: '2026-07-01'
---

As the storage engine, I want encryption applied per policy (spec Section 9.5) inside the block write/read pipeline so that encryption is a first-class path: Default and Full policies, per-block Encrypted flag + epoch stamping, checksum-on-ciphertext ordering, distinct error classes.