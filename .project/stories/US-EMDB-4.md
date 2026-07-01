---
acceptance_criteria:
- Single Protobuf library chosen and documented in architecture decisions
- Duplicate model files removed — one canonical location
- All Protobuf models have correct serialization attributes
- Project compiles cleanly with no ambiguous type references
created: '2026-02-22'
epic_id: EPIC-EMDB-2
id: US-EMDB-4
points: 5
priority: must
status: done
tags: []
title: Consolidate Protobuf library choice and remove duplicates
updated: '2026-02-22'
---

As a developer, I want a single consistent Protobuf library and no duplicate model definitions so that serialization is predictable and maintainable.

Currently both Google.Protobuf and protobuf-net are referenced. Models are duplicated in EmailDB.Format.Protobuf/Models/ and EmailDB.Format.Protobuf/Models/Blocks/. Need to pick one library, remove the other, and consolidate duplicate model files.