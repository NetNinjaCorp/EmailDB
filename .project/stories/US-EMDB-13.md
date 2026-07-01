---
acceptance_criteria:
- All test projects compile without errors
- Existing passing tests continue to pass
- Test helper infrastructure (TestHelpers.cs) works with current APIs
- Mock setups reference correct current types
- dotnet test runs without compilation failures
created: '2026-02-22'
epic_id: EPIC-EMDB-6
id: US-EMDB-13
points: 5
priority: must
status: backlog
tags: []
title: Fix broken test references and build test infrastructure
updated: '2026-02-22'
---

As a developer, I want all test projects to compile and run so that we have a working test baseline.

BlockManagerTests references TestBlockManager which doesn't exist. BasicFileTests references a StorageManager constructor that's out of date. StorageManagerTests references types that may have moved. Unit test mocks reference BlockContent/BlockHeader types from old locations. Need to fix all references to match the current refactored code structure (FileManagement/ namespace, new helpers, etc.).