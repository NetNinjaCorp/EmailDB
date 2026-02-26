# EmailDB — Vision

## Goal
Build a high-performance, single-file email storage engine that provides efficient storage, retrieval, and search of email data with full integrity guarantees.

## Key Principles
1. **Single-file simplicity** — everything in one `.emdb` file, no external dependencies
2. **Append-only durability** — never overwrite, always append for crash safety
3. **Pluggable serialization** — swap formats without changing core logic
4. **Layered architecture** — clean separation of concerns from raw I/O to email API
5. **Performance** — Custom append-only B+-tree indexing, three-tier storage for efficient folder listing
6. **Encryption** — AES-256-GCM per-block encryption with KEK/DEK key wrapping and multi-epoch rotation

## Success Criteria
- Reliable email storage with BLAKE3-128 integrity verification and Merkle tree validation
- Fast read/write operations with caching and WAL-buffered BTree flushes
- Three-tier email model: packed folder listing pages (Tier 1), email metadata (Tier 2), raw content (Tier 3)
- Efficient tiered compaction to reclaim space from copy-on-write dead blocks
- Multi-phase search: address trigram index, listing page scan, date BTree, vector embeddings, bloom filters
- Active-to-backup replication via ULID high-water mark and FolderVersion counters
- Clean API surface via EmailManager for consuming applications
