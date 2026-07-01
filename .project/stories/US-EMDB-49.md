---
acceptance_criteria:
- HeaderChecksumSize and PayloadChecksumSize constants are 16
- TotalFixedOverhead is 84
- ComputeChecksum returns byte[16] using BLAKE3
- Write path writes 16-byte checksums
- Read path reads and verifies 16-byte checksums
- Scan and TryReadBlockLocation use 16-byte checksums
- Force.Crc32 import removed from RawBlockManager
- Block.HeaderChecksum and PayloadChecksum are byte[] not uint
- Empty payload uses 16 zero bytes as checksum
created: '2026-02-24'
epic_id: EPIC-EMDB-10
id: US-EMDB-49
points: 5
priority: must
status: done
tags: []
title: Update Block model and RawBlockManager for BLAKE3-128 checksums
updated: '2026-02-24'
---

As a developer, I want the block format to use BLAKE3-128 (16-byte) checksums instead of CRC32 (4-byte) so that block integrity verification is cryptographically strong and consistent with the B+-tree hash chain design.

Changes:
- Block.cs: HeaderChecksum and PayloadChecksum change from uint to byte[16]
- RawBlockManager constants: HeaderChecksumSize 4→16, PayloadChecksumSize 4→16, TotalFixedOverhead 60→84
- RawBlockManager.ComputeChecksum(): returns byte[16] via Blake3.Hasher.Hash() truncated to 16 bytes instead of Crc32Algorithm.Compute()
- WriteBlockToStream(): write 16-byte checksums instead of 4-byte uint
- ReadBlockFromStreamInternal(): read 16 bytes and compare via SequenceEqual instead of uint comparison
- ScanExistingBlocksAsync(): update header checksum reading from ReadUInt32 to ReadBytes(16)
- TryReadBlockLocation(): update accessor.ReadUInt32 to ReadArray for 16-byte checksum
- ReadUInt32FromFileStream(): replace with ReadChecksumFromFileStream returning byte[16]
- Remove `using Force.Crc32;`, use existing `Blake3` package (already a dependency)
- Empty payload checksum: use 16 zero bytes instead of 0U