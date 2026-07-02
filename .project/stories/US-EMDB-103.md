---
acceptance_criteria:
- Every project on disk is in the sln and vice versa
- No folder/project name mismatches remain
- PROJECT.md project table matches the sln
created: '2026-07-02'
depends_on: []
epic_id: EPIC-EMDB-22
id: US-EMDB-103
points: 3
priority: should
status: backlog
tags:
- v3
- cleanup
- hygiene
title: Solution hygiene
updated: '2026-07-02'
---

As a maintainer, I want the solution to match reality: rename or remove the EmailDB.Testing.CapnProtoFile folder (contains EmailDB.Testing.RawBlocks.csproj), remove ghost dirs (EmailDB.RyanTesting, Tests/), align the sln project list, and fix the PROJECT.md project table.