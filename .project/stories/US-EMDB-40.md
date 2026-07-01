---
acceptance_criteria:
- Default policy encrypts EmailContent Folder FolderTree Segment WAL
- Full policy encrypts all block types except header Metadata at offset 0
- ShouldEncrypt returns correct result per policy
- Custom policies can be constructed
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-40
points: 2
priority: must
status: done
tags: []
title: Implement EncryptionPolicy for block type filtering
updated: '2026-02-24'
---

As a developer, I want an encryption policy system so that I can control which block types get encrypted, allowing metadata to remain readable while sensitive content is protected.