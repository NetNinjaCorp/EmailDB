---
acceptance_criteria:
- Every B+-tree block contains PrevChainHash linking to previous block
- Modifying any block's payload causes chain verification failure
- Internal node child hashes match actual child node content hashes
- Root hash in IndexRoot matches actual root node hash
- Quick verification detects tampered IndexRoot chain
- Standard verification detects tampered node on key lookup path
- Full verification traverses all nodes and reports first integrity violation
- Chain handles genesis block (first block has zeroed PrevChainHash)
- Compaction produces valid new chain with genesis
created: '2026-02-23'
epic_id: EPIC-EMDB-8
id: US-EMDB-29
points: 8
priority: must
status: done
tags: []
title: Implement BLAKE3 hash chaining and Merkle integrity verification
updated: '2026-02-23'
---

As a security-conscious developer, I want every B+-tree block to be hash-chained and Merkle-verified so that tampering with any block is detectable from the root hash.

**Scope**:
- **Block-level chain**: Every B+-tree block (leaf, internal, IndexRoot) includes PrevChainHash — the BLAKE3 hash of the immediately preceding block written to the file. This creates a sequential chain.
- **Merkle tree**: Internal nodes include BLAKE3 hashes of each child node's content. The root node's NodeContentHash represents the integrity of the entire subtree.
- **IndexRoot integrity**: IndexRoot.RootNodeHash = BLAKE3 of root node. IndexRoot.PreviousRootHash = BLAKE3 of previous IndexRoot block. Enables chain verification across flush boundaries.
- **Verification modes**:
  - Quick: Verify IndexRoot chain (O(flush_count))
  - Standard: Verify root → leaf Merkle path for a specific key (O(height))
  - Full: Verify entire tree Merkle integrity (O(n nodes))
- **Hash computation**: BLAKE3 via Blake3.NET, computed over node payload bytes (after serialization, before block wrapping)
- **Chain continuity across compaction**: After compaction, new chain starts with a genesis hash; old chain is sealed