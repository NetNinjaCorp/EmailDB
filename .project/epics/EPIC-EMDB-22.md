---
created: '2026-07-01'
id: EPIC-EMDB-22
points: null
priority: must
status: draft
tags:
- v3
- cleanup
- testing
- tech-debt
target_date: null
title: Legacy Retirement &amp; Test Migration
updated: '2026-07-01'
---

Remove the v1-era code and rebase the test suite on v3 (ADR-016 retirement list): delete ZonetreeSegmentIO, commented-out EmailManager/MaintenanceManager, Segment/Folder managers and models, duplicate EmailDB.Format.Protobuf models, int64 BlockIdGenerator range partitioning, OverrideLocation and raw-region WAL paths; fix solution hygiene (CapnProtoFile folder naming, ghost projects, PROJECT.md project table); migrate the ~140 unit tests to the v3 stack, keeping the crypto-primitive tests that remain valid. Success: solution builds clean with zero dead files and a green v3 test suite.