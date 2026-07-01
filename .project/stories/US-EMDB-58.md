---
acceptance_criteria:
- BlockType.EmailMetadata = 11 added to BlockType enum
- EmailMetadataContent model defined with all fields (EmailHashedID Subject From To
  Cc Date AttachmentCount ContentSize Flags ContentBlockId FolderPath)
- Protobuf-annotated model in EmailDB.Format.Protobuf.Models with ProtoContract/ProtoMember
  attributes
- EmailMetadataContent round-trips correctly through ProtobufBlockContentSerializer
- BlockConverter handles BlockType.EmailMetadata deserialization
- Existing tests pass unchanged
created: '2026-02-25'
epic_id: EPIC-EMDB-11
id: US-EMDB-58
points: 3
priority: must
status: backlog
tags: []
title: Define EmailMetadata block type and model
updated: '2026-02-25'
---

As a developer, I want a new BlockType.EmailMetadata and an EmailMetadataContent model so that email header/envelope data can be stored separately from email content.

**New block type:** `BlockType.EmailMetadata = 11`

**EmailMetadataContent model fields:**
- EmailHashedID (32 bytes) — links back to BTree key
- Subject (string)
- From (string)
- To (string)
- Cc (string)
- Date (DateTime)
- AttachmentCount (int)
- ContentSize (long) — size of the full content block payload
- Flags (byte) — read/unread, starred, etc.
- ContentBlockId (long) — pointer to the heavy EmailContent block
- FolderPath (string) — which folder this email currently lives in

**Protobuf model:** Create matching Protobuf-annotated model in EmailDB.Format.Protobuf.Models for serialization.

**This is the foundational model change — no behavior changes, just types and serialization.**