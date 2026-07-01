---
acceptance_criteria:
- ProtobufBlockContentSerializer implements IBlockContentSerializer
- All 6 content types serialize/deserialize correctly via Protobuf
- PayloadEncoding enum is respected when reading blocks
- JSON serializer retained as fallback for debugging
- Round-trip tests pass for all content types
created: '2026-02-22'
epic_id: EPIC-EMDB-2
id: US-EMDB-5
points: 5
priority: must
status: done
tags: []
title: Implement IBlockContentSerializer with Protobuf adapter
updated: '2026-02-22'
---

As a developer, I want the IBlockContentSerializer/IPayloadEncoding to use actual Protobuf serialization so that block payloads are efficiently encoded.

DefaultBlockContentSerializer currently uses System.Text.Json only. Need to implement a ProtobufBlockContentSerializer that uses the chosen Protobuf library. The serializer should handle all BlockType content models (MetadataContent, FolderContent, FolderTreeContent, SegmentContent, WALContent, HeaderContent).