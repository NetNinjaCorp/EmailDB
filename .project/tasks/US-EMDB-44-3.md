---
assignee: claude
created: '2026-02-24'
id: US-EMDB-44-3
points: 2
status: done
story_id: US-EMDB-44
title: 'Test: Read methods read key epoch from Flags and decrypt before deserialization'
updated: '2026-02-25'
---

Verify B-tree read methods extract the key epoch from Flags bits 1-7, pass it to KeyWrappingEncryptionProvider to look up the correct DEK, and decrypt before deserializing the node.