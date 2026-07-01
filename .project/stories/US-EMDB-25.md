---
acceptance_criteria:
- BlockType enum extended with BTreeLeaf/BTreeInternal/IndexRoot/EmailContent
- Leaf node binary layout serializes and deserializes correctly with 82 entries at
  4036B
- Internal node binary layout serializes and deserializes correctly with 55 children
  + Merkle hashes at 4036B
- IndexRoot payload round-trips correctly
- BLAKE3 hashing produces correct 32-byte digests for node content
- BlockIdGenerator allocates IDs in a dedicated B+-tree range without collisions
- Custom binary serializer is at least 3x faster than protobuf for fixed-size node
  data
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-25
points: 5
priority: must
status: done
tags: []
title: Define B+-tree block types, node structures, and binary serialization
updated: '2026-02-23'
---

As a storage engine developer, I want well-defined block types and binary serialization for B+-tree nodes so that the index can be stored as first-class blocks in the .emdb file.

**Scope**:
- Add new BlockType enum values: BTreeLeaf(6), BTreeInternal(7), IndexRoot(8), EmailContent(9)
- Define binary layout for leaf nodes (NodeType, Version, EntryCount, NodeContentHash, PrevChainHash, entries[])
- Define binary layout for internal nodes (NodeType, Version, KeyCount, NodeContentHash, PrevChainHash, keys[], childOffsets[], childHashes[])
- Define IndexRoot payload (RootNodeBlockOffset, EntryCount, TreeHeight, RootNodeHash, PreviousRootHash, PreviousRootOffset)
- Implement BTreeNodeSerializer: custom BinaryWriter/BinaryReader serialization (NOT protobuf — fixed-size fields)
- Add BLAKE3 NuGet dependency (Blake3.NET by xoofx)
- Integrate with existing BlockIdGenerator (new ID range for B+-tree nodes)
- All nodes must fit within 4096-byte block payloads (4096 - 60 byte block overhead = 4036 usable)