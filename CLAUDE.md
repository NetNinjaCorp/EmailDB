# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

EmailDB is a high-performance, specialized database system for email storage and retrieval using a revolutionary hybrid architecture:
- **Append-Only Block Storage**: 99.6% storage efficiency
- **ZoneTree B+Tree Indexes**: Sub-millisecond lookups
- **Hash Chain Integrity**: Cryptographic proof of authenticity
- **Performance**: 50+ MB/s writes, 50,000+ queries/second

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
2. **BlockManager**: Serialization layer (JSON/Protobuf)
3. **CacheManager**: In-memory LRU caching
4. **MetadataManager**: System metadata and configuration
5. **FolderManager**: Email folder hierarchy
6. **EmailManager**: High-level email operations
7. **HybridEmailStore**: Top-level API combining all managers

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

### Current Architecture Enhancement (TODO.md)
- All data in blocks (emails, folders, envelopes)
- 127 compression + 127 encryption algorithms
- Email batching with adaptive sizing (50MB-1GB)
- Compound Email IDs (BlockId:LocalId)
- In-band key management

## Testing Strategy
- Unit tests for each component
- Integration tests for manager interactions
- Performance benchmarks
- Cross-platform CI/CD (Linux, Windows, macOS)

## Development Notes
- Check TODO.md for current implementation priorities
- Review PHASE1-7_IMPLEMENTATION_PLAN.md for detailed roadmaps
- Follow Result<T> pattern for all fallible operations
- Maintain append-only design for data integrity