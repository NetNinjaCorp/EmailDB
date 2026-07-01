---
acceptance_criteria:
- FlagAlgorithm constant removed from Block.cs
- 'Block.KeyEpoch property reads bits 1-7: (Flags >> 1) & 0x7F'
- Block.SetKeyEpoch method writes epoch into bits 1-7 preserving bit 0
- Unencrypted blocks have Flags = 0 and KeyEpoch = 0
- Encrypted blocks have bit 0 set and KeyEpoch matches the active epoch at write time
- All existing block tests updated and passing
created: '2026-02-24'
epic_id: EPIC-EMDB-9
id: US-EMDB-54
points: 3
priority: must
status: done
tags: []
title: Update Block Flags for Key Epoch encoding
updated: '2026-02-24'
---

As a developer, I want the block header Flags byte to encode the key epoch (bits 1-7) alongside the encrypted flag (bit 0), so that each encrypted block self-identifies which DEK was used, enabling multi-key decryption without external metadata.