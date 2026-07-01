---
acceptance_criteria:
- RawBlockManagerTests WriteTestBlock helper writes BLAKE3-128 checksums
- RawBlockManagerTests ReadTestBlock helper reads BLAKE3-128 checksums
- BTreeWALManagerTests corruption test updated for new checksum offsets
- All existing unit tests pass with zero regressions
- Crc32.NET package reference removed from EmailDB.UnitTests.csproj
- No remaining references to Force.Crc32 in test projects
created: '2026-02-24'
epic_id: EPIC-EMDB-10
id: US-EMDB-50
points: 5
priority: must
status: done
tags: []
title: Update RawBlockManager unit tests for BLAKE3-128 checksums
updated: '2026-02-24'
---

As a developer, I want all existing RawBlockManager tests to pass with the new BLAKE3-128 checksum format so that the block I/O layer remains fully validated.

Key test files affected:
- RawBlockManagerTests.cs: WriteTestBlock() and ReadTestBlock() helper methods use Force.Crc32.Crc32Algorithm.Compute() and write/read 4-byte checksums. Must switch to BLAKE3-128 (16-byte) checksums.
- BTreeWALManagerTests.cs: Corruption test at line ~2056 flips payload bytes to break "CRC32 payload checksum". Must update comments and corruption logic to account for 16-byte checksum offset change.
- BlockManagerTests.cs: Any tests that manually construct block bytes or verify checksums.
- All tests that assert on TotalFixedOverhead (57→81) or checksum sizes.

Changes per file:
- Replace `using Force.Crc32;` with `using Blake3;`
- Replace `Crc32Algorithm.Compute(data)` with `Hasher.Hash(data).AsSpan().Slice(0, 16).ToArray()`
- Replace `writer.Write(uint checksum)` with `writer.Write(byte[16] checksum)`
- Replace `reader.ReadUInt32()` with `reader.ReadBytes(16)` for checksum reads
- Update block layout offset comments (Header(36) + Checksum(16) + Payload(N) + Checksum(16) + Footer(16))
- Remove Crc32.NET package reference from EmailDB.UnitTests.csproj