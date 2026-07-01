---
created: '2026-02-22'
id: EPIC-EMDB-2
points: null
priority: must
status: archived
tags:
- serialization
- architecture
target_date: null
title: Serialization Layer Consolidation
updated: '2026-07-01'
---

Resolve the dual-library confusion (Google.Protobuf vs protobuf-net), consolidate duplicate model definitions, implement proper IPayloadEncoding/IBlockContentSerializer adapters, and decide the future of the Cap'n Proto layer. Currently using JSON serialization as a placeholder — need to wire up the intended Protobuf serialization.