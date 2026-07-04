---
acceptance_criteria:
- Round-trip write/read for every block type and encoding
- Header checksum verified before trusting any header field and PayloadLength validated
  against MaxPayloadLength before allocation
- Payload checksum covers on-disk bytes and empty payload stores 16 zero bytes
- Footer TotalBlockLength supports backward walk from EOF
- ULID generator stays monotonic under simulated clock regression
- Compression byte round-trips None/LZ4/Zstd with decompression bomb guard
created: '2026-07-01'
depends_on: []
epic_id: EPIC-EMDB-12
id: US-EMDB-64
points: 8
priority: must
status: done
tags:
- v3
- blocks
- format
title: v3 block writer and reader (ULID, checksums, length sanity)
updated: '2026-07-03'
---

As the storage engine, I want v3 block append and read per spec Section 4 so that every block is integrity-checked and identified by ULID. 48-byte header (magic, version, type, flags, encoding, compression, 2-byte KeyEpoch, 16-byte ULID, payload length, reserved), BLAKE3-128 header and payload checksums, 16-byte footer with TotalBlockLength, monotonic ULID generator.