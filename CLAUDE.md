# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

EmailDB is a high-performance, specialized database system for email storage and retrieval using a revolutionary hybrid architecture:
- **Append-Only Block Storage**: 99.6% storage efficiency
- **ZoneTree B+Tree Indexes**: Sub-millisecond lookups
- **Hash Chain Integrity**: Cryptographic proof of authenticity
- **Performance**: 50+ MB/s writes, 50,000+ queries/second
- **Encryption**: Built-in encryption with key management (AES-256-GCM, ChaCha20-Poly1305, etc.)
- **Format Versioning**: Version-aware database with migration support

## Essential Commands

```bash
# Build
dotnet build

# Run all tests
dotnet test

# Run specific test categories
dotnet test --filter "FullyQualifiedName~HybridEmailStore"
dotnet test --filter "FullyQualifiedName~Performance"
dotnet test --filter "FullyQualifiedName~Phase1"  # Phase-specific tests (1-4)

# Run demos
dotnet run --project EmailDB.Console
```

## Architecture

### Component Hierarchy (bottom-up)
1. **RawBlockManager**: Low-level block I/O with checksums
2. **BlockManager**: Serialization layer (JSON/Protobuf) with compression/encryption
3. **CacheManager**: In-memory LRU caching
4. **MetadataManager**: System metadata and configuration
5. **FolderManager**: Email folder hierarchy
6. **EmailManager**: High-level email operations
7. **HybridEmailStore**: Top-level API combining all managers
8. **EncryptionKeyManager**: Master key and data key management
9. **EmailDatabase**: Version-aware database with migration support

### Key Design Patterns
- **Result Pattern**: All operations return `Result<T>` for explicit error handling
- **Block-Based Storage**: 57-byte overhead, self-contained checksummed blocks
- **Append-Only Design**: Data integrity through immutability
- **AsyncReaderWriterLock**: Thread-safe concurrent access

### Block Format
```
[Version(1)] [Type(1)] [Flags(2)] [BlockId(8)] [PrevBlockId(8)] 
[Timestamp(8)] [Checksum(4)] [CompressedSize(4)] [UncompressedSize(4)]
[PayloadHash(16)] [PayloadFormat(1)] [CompressionType(1)] [EncryptionType(1)]
[Payload(variable)]
```

### Current Architecture Enhancement (Stages 1-5 COMPLETED)
- All data in blocks (emails, folders, envelopes)
- 127 compression + 127 encryption algorithms
- Email batching with adaptive sizing (50MB-1GB)
- Compound Email IDs (BlockId:LocalId)
- In-band key management with EncryptionKeyManager
- Format versioning with compatibility matrix
- Version-aware search and migration framework

## Testing Strategy
- Unit tests for each component
- Integration tests for manager interactions
- Performance benchmarks
- Cross-platform CI/CD (Linux, Windows, macOS)
- Encryption/compression round-trip tests
- Version migration tests

## Development Notes
- Check TODO.md for current implementation priorities (Phases 1-5 completed)
- Review PHASE1-7_IMPLEMENTATION_PLAN.md for detailed roadmaps
- Follow Result<T> pattern for all fallible operations
- Maintain append-only design for data integrity
- Encryption keys are never stored in plaintext
- Use EncryptionKeyManager for all key operations

## Git Commit Guidelines
- **NEVER include self-attribution in commit messages**
- Do NOT add "Generated with Claude Code" or "Co-Authored-By: Claude" 
- Keep commit messages concise and descriptive
- Focus on what changed and why, not who made the change
- Use conventional commit format when appropriate

## Known Issues
- **ZstdSharp Memory Issues**: ZstdSharp library has AccessViolationException on some platforms/data sizes
  - Likely buffer overflow in native ZstdSharp implementation
  - 90.6% compression tests pass, Zstd-specific failures are platform-dependent
  - Workaround: Use Gzip, LZ4, or Brotli compression instead
  - Status: Third-party library issue, not fixable in EmailDB codebase