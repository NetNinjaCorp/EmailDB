# EmailDB Development TODO List

## Mission
Transform EmailDB into a bulletproof email archive specification with potential to become a general-purpose storage format.

## Critical Format Fixes (Priority: URGENT)

### 1. Fix Block Header Structure ⚠️
- [ ] Add PayloadEncoding field at byte position 12 in Block class
- [ ] Update RawBlockManager to read/write PayloadEncoding field
- [ ] Update header size constants and offsets
- [ ] Ensure backward compatibility or version migration

### 2. Implement IPayloadEncoding Interface 🔧
- [ ] Create IPayloadEncoding interface matching architecture docs
- [ ] Implement ProtobufPayloadEncoding
- [ ] Implement JsonPayloadEncoding
- [ ] Implement CapnProtoPayloadEncoding
- [ ] Implement RawBytesPayloadEncoding
- [ ] Wire up encoding selection in BlockManager

### 3. Add Missing Block Types 📦
- [ ] Add ZoneTreeSegment_KV to BlockType enum
- [ ] Add ZoneTreeSegment_Vector to BlockType enum
- [ ] Add FreeSpace block type for compaction

## Testing Infrastructure (Priority: HIGH)

### Unit Testing
- [ ] Block format compliance tests (every byte verified)
- [ ] Header serialization/deserialization tests
- [ ] Payload encoding round-trip tests
- [ ] Checksum calculation and verification tests
- [ ] Magic number validation tests
- [ ] Block boundary tests

### Fuzz Testing
- [ ] Random byte corruption detection
- [ ] Truncated file handling
- [ ] Bit flip resilience
- [ ] Invalid magic number recovery
- [ ] Checksum mismatch handling
- [ ] Block size overflow tests

### Property-Based Testing
- [ ] Serialization invariants
- [ ] Read/write symmetry
- [ ] Block ID uniqueness
- [ ] Timestamp monotonicity
- [ ] File size calculations

### Integration Testing
- [ ] Multi-threaded read access
- [ ] Concurrent write serialization
- [ ] Reader-writer lock correctness
- [ ] Transaction isolation
- [ ] Deadlock prevention

### Performance Testing
- [ ] Sequential write throughput
- [ ] Random read latency
- [ ] Block size impact analysis
- [ ] Memory usage profiling
- [ ] Cache hit rates
- [ ] Compaction performance

### Corruption & Recovery Testing
- [ ] Partial write recovery
- [ ] Power failure simulation
- [ ] Disk full handling
- [ ] Bad sector simulation
- [ ] File system corruption
- [ ] Compaction failure recovery

## Portability & Compatibility (Priority: HIGH)

### Cross-Platform Testing
- [ ] Windows NTFS behavior
- [ ] Linux ext4/XFS behavior
- [ ] macOS APFS behavior
- [ ] Network file system support
- [ ] Path separator handling
- [ ] File locking semantics

### Binary Format Portability
- [ ] Little-endian verification
- [ ] Big-endian compatibility tests
- [ ] 32-bit platform support
- [ ] ARM architecture testing
- [ ] Struct padding verification

### Version Compatibility
- [ ] Forward compatibility tests
- [ ] Backward compatibility tests
- [ ] Version upgrade scenarios
- [ ] Graceful degradation
- [ ] Version negotiation

## Scalability Testing (Priority: MEDIUM)

### Large File Support
- [ ] 100GB+ file handling
- [ ] 1TB+ stress tests
- [ ] Memory-mapped file optimization
- [ ] Streaming read/write support
- [ ] Partial file loading

### Performance Optimization
- [ ] Block caching strategies
- [ ] Read-ahead optimization
- [ ] Write coalescing
- [ ] Parallel compaction
- [ ] Index optimization

## ZoneTree Integration (Priority: MEDIUM)

### Storage Provider Implementation
- [ ] Implement IRandomAccessDevice for blocks
- [ ] Implement IFileStreamProvider
- [ ] Map ZoneTree segments to blocks
- [ ] Handle ZoneTree WAL in blocks
- [ ] Test ZoneTree operations

### Email Search Integration
- [ ] Full-text index storage
- [ ] Vector similarity search
- [ ] Metadata indexing
- [ ] Query optimization
- [ ] Index compaction

## Tooling & Validation (Priority: MEDIUM)

### Format Validation Tool
- [ ] Command-line validator
- [ ] Block integrity checker
- [ ] File structure analyzer
- [ ] Repair suggestions
- [ ] Performance diagnostics

### Development Tools
- [ ] Block format visualizer
- [ ] Test data generator
- [ ] Corruption injector
- [ ] Performance profiler
- [ ] Debug dump analyzer

## Documentation (Priority: HIGH)

### Specification Documents
- [ ] Update format spec to v1.0
- [ ] Create implementation guide
- [ ] Write best practices guide
- [ ] Document error codes
- [ ] Create FAQ

### API Documentation
- [ ] XML documentation for all public APIs
- [ ] Usage examples
- [ ] Performance characteristics
- [ ] Thread safety guarantees
- [ ] Error handling patterns

### Test Documentation
- [ ] Test coverage reports
- [ ] Performance baselines
- [ ] Known limitations
- [ ] Platform-specific notes

## Future Enhancements (Priority: LOW)

### Security Features
- [ ] Block-level encryption
- [ ] Digital signatures
- [ ] Access control
- [ ] Audit logging
- [ ] Tamper detection

### Advanced Features
- [ ] Compression support
- [ ] Deduplication
- [ ] Snapshotting
- [ ] Replication
- [ ] Cloud storage backends

## Success Criteria

### Correctness
- Zero data loss under any circumstances
- 100% spec compliance
- Deterministic behavior
- Proper error handling

### Performance
- 1GB/s+ sequential write
- <1ms random read latency
- <100MB memory overhead
- Linear scaling to TB sizes

### Reliability
- Survive power loss
- Recover from corruption
- Handle disk errors
- Support hot backups

### Usability
- Clear error messages
- Simple API
- Good documentation
- Cross-platform support

## Testing Priorities

1. **Format Correctness** - The spec must be rock solid
2. **Data Integrity** - Never lose or corrupt data
3. **Concurrent Access** - Thread-safe operations
4. **Error Recovery** - Graceful failure handling
5. **Performance** - Meet throughput targets
6. **Portability** - Work everywhere

## Next Steps

1. Fix the block header structure (add PayloadEncoding)
2. Create comprehensive unit tests for current implementation
3. Set up CI/CD with test automation
4. Implement fuzz testing framework
5. Begin stress testing with large files

This is our roadmap to making EmailDB a production-ready, bulletproof storage specification.