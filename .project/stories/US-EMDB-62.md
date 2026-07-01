---
acceptance_criteria:
- FolderTreeContent serializable by ProtobufBlockContentSerializer
- FolderContent serializable by ProtobufBlockContentSerializer with EmailHashedID
  support
- FolderManager.CreateFolderAsync works on a fresh file after InitializeFileAsync
- FolderManager move/delete/add email operations persist correctly to disk
- File reopen loads folder tree and folder contents correctly
- FolderContent.EmailIds stores EmailHashedID consistently across base and Protobuf
  models
created: '2026-02-25'
epic_id: EPIC-EMDB-11
id: US-EMDB-62
points: 5
priority: must
status: backlog
tags: []
title: Fix FolderManager serialization for Protobuf compatibility
updated: '2026-02-25'
---

As a developer, I want FolderManager operations to work correctly with ProtobufBlockContentSerializer so that folder create/move/delete operations persist to disk.

**Current issue:** The FolderManager uses base types from EmailDB.Format.Models.BlockTypes (FolderTreeContent, FolderContent) which lack Protobuf attributes. The Protobuf-annotated versions exist in EmailDB.Format.Protobuf.Models but are separate classes. CacheManager.UpdateFolderTree() fails with "Type is not expected, and no contract can be inferred" when using ProtobufBlockContentSerializer.

**Options:**
1. Add ProtoContract/ProtoMember attributes to the base types in EmailDB.Format.Models.BlockTypes
2. Use the Protobuf model types throughout the folder operations
3. Add a mapping layer between base types and Protobuf types

**Also fix:** FolderManager.CreateFolderAsync fails on fresh files because GetCachedFolderTree() returns null when HeaderContent.FirstFolderTreeOffset == -1. Need an initialization path that bootstraps an empty FolderTreeContent on new files (during MetadataManager.InitializeFileAsync or similar).

**FolderContent.EmailIds type alignment:** The base FolderContent uses `List<EmailHashedID>` while the Protobuf version uses `List<long>`. These need to be unified.