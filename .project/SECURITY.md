# Security

EmailDB stores email archives in a single-file format; security is designed into the on-disk format itself. The normative details live in `EmailDB_FileFormat_Spec.md` and `docs/Encryption.md` (including the threat model) — this doc is a summary.

## Encryption

- **Per-block encryption**: AES-256-GCM with a KEK/DEK key-wrapping hierarchy and multi-epoch key rotation (2-byte KeyEpoch in every block header).
- **AAD binding**: ciphertext is bound to block identity and file identity, preventing block relocation/substitution across files.
- **Password change / key rotation**: handled via KEK re-wrap and epoch rotation without rewriting all data; compaction can re-encrypt (see `docs/Compaction.md`).

## Integrity

- **BLAKE3-128 checksums** on every block header and payload; header checksum is verified before any header field is trusted.
- **Merkle-verified B+-tree indexes**: path verification on read is mandatory; tamper of any node fails the affected lookups.
- **Dual-slot superblock** with slot checksums guards against torn writes on the only in-place structure.

## Operational Safety

- **Single-writer OS lock**: concurrent writers fail fast; readers open shared.
- **fsync-is-fatal discipline**: fsync failures poison the handle and force recovery on reopen — durability errors are never papered over.
- **Length-sanity validation**: PayloadLength checked against MaxPayloadLength before allocation; decompression bomb guard on compressed payloads.

## Secrets Hygiene

- No credentials, API keys, or user secrets belong in this repo; test fixtures use synthetic data only.
