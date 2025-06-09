# EmailDB Implementation TODO

## Overview
Refactor EmailDB to use HybridEmailStore architecture with all data stored in append-only blocks, including folder metadata and email envelopes for efficient listings.

## Key Architectural Decisions

### Storage Architecture
- **All data in append-only blocks**: Emails, folders, envelopes, and keys stored as blocks
- **ZoneTree for indexes only**: Indexes point to block locations, not data
- **Hash chain integrity**: SHA-256 hash chain provides cryptographic integrity
- **Envelope blocks**: Lightweight metadata for fast folder listings without loading full emails
- **Email batching**: Multiple emails per block with adaptive sizing (50MB → 1GB)
- **Compound Email IDs**: BlockId:LocalId for efficient lookups

### Compression & Encryption
- **Flexible algorithm support**: 127 compression and 127 encryption algorithms via Flags field
- **Compress-then-encrypt**: Data compressed first, then encrypted for better security
- **In-band key management**: All encryption keys stored within database as append-only blocks
- **Multi-layer security**: User auth → Master key → Data keys

### Key Management
- **Multiple unlock methods**: Password, WebAuthn, PGP, smart cards, etc.
- **Master key architecture**: Single master key unlocks all data encryption keys
- **Key rotation support**: Old keys preserved for recovery in append-only fashion
- **Self-contained**: No external key storage required

## Phase 1: Core Architecture Refactoring

### 1.1 Block Type Definitions
- [ ] Create `EmailEnvelope` model in `Models/EmailContent/`
  - [ ] Define fields: EmailId, MessageId, Subject, From, To, Date, Size, HasAttachments, Flags
  - [ ] Add serialization support
- [ ] Create `FolderEnvelopeBlock` in `Models/BlockTypes/`
  - [ ] Define structure with FolderPath, Version, List<EmailEnvelope>
  - [ ] Implement BlockContent interface
- [ ] Update `FolderContent` in `Models/BlockTypes/`
  - [ ] Add EnvelopeBlockId field
  - [ ] Add Version field for superseding support
  - [ ] Add LastModified timestamp
- [ ] Create missing ZoneTree block types
  - [ ] `ZoneTreeSegmentKVContent` for key-value storage
  - [ ] `ZoneTreeSegmentVectorContent` for vector storage
  - [ ] Implement proper serialization for both
- [ ] Add persistent block index support
  - [ ] Design index file format for fast block lookups
  - [ ] Implement index persistence on block writes
  - [ ] Add index loading on startup
  - [ ] Create index rebuild capability

### 1.2 Email Storage and Batching
- [ ] Implement Email ID system
  - [ ] Create `EmailHashedID` with envelope and content hashes
  - [ ] Include more fields to prevent collisions (MessageId, From, To, Date, Subject, Cc, InReplyTo, Size)
  - [ ] Implement compound key system (BlockId:LocalId)
  - [ ] Add duplicate detection using dual-hash approach
- [ ] Create adaptive block sizing
  - [ ] Implement `AdaptiveBlockSizer` with size progression:
    - [ ] < 5GB: 50MB blocks
    - [ ] < 25GB: 100MB blocks
    - [ ] < 100GB: 250MB blocks
    - [ ] < 500GB: 500MB blocks
    - [ ] >= 500GB: 1GB blocks
  - [ ] Soft limits - blocks can be slightly over/under target size
  - [ ] No email splitting across blocks
- [ ] Implement email batching system
  - [ ] Create `EmailBlockBuilder` for accumulating emails
  - [ ] Automatic flush when reaching target size
  - [ ] Serialize multiple emails per block
  - [ ] Track pending IDs until block is written
- [ ] Update storage manager
  - [ ] Coordinate block sizing based on database growth
  - [ ] Handle email addition with deduplication checks
  - [ ] Finalize email IDs after block write
  - [ ] Update all relevant indexes atomically

### 1.3 Serialization Infrastructure
- [ ] Use existing Protobuf serialization
  - [ ] Ensure all new block types have Protobuf definitions
  - [ ] Reuse existing binary walking functions
  - [ ] Maintain compatibility with current block format
- [ ] Update `DefaultBlockContentSerializer`
  - [ ] Refactor to use `IPayloadEncoding` interface
  - [ ] Remove dependency on old `iBlockContentSerializer`
  - [ ] Ensure backward compatibility

### 1.4 Compression and Encryption Infrastructure

#### Block Format Enhancement
- [ ] Update Block header to utilize Flags field (32 bits)
  - [ ] Define BlockFlags enum with compression/encryption bits
  - [ ] Support 127 compression algorithms (7 bits)
  - [ ] Support 127 encryption algorithms (7 bits)
  - [ ] Reserve remaining bits for future use
- [ ] Create ExtendedBlockHeader for compressed/encrypted blocks
  - [ ] UncompressedSize field for compressed blocks
  - [ ] IV, AuthTag, and KeyId fields for encrypted blocks
  - [ ] Variable length based on flags

#### Compression System
- [ ] Create `ICompressionProvider` interface
  - [ ] Define Compress/Decompress methods with Result<T> pattern
  - [ ] Include algorithm ID and name properties
- [ ] Implement compression providers
  - [ ] `GzipCompressionProvider` (ID: 1)
  - [ ] `LZ4CompressionProvider` (ID: 2)
  - [ ] `ZstdCompressionProvider` (ID: 3)
  - [ ] `BrotliCompressionProvider` (ID: 4)
- [ ] Create `AlgorithmRegistry` for managing providers
  - [ ] Registration system for compression algorithms
  - [ ] Lookup by algorithm ID
  - [ ] Extensible for future algorithms

#### Encryption System
- [ ] Create `IEncryptionProvider` interface
  - [ ] Define Encrypt/Decrypt methods with Result<T> pattern
  - [ ] Include IV size, auth tag size properties
  - [ ] Support key ID for multi-key scenarios
- [ ] Implement encryption providers
  - [ ] `AES256GcmEncryptionProvider` (ID: 1)
  - [ ] `ChaCha20Poly1305EncryptionProvider` (ID: 2)
  - [ ] `AES256CbcHmacEncryptionProvider` (ID: 3)
- [ ] Create `BlockProcessor` for compression/encryption pipeline
  - [ ] Compress then encrypt workflow
  - [ ] Decrypt then decompress workflow
  - [ ] Handle extended headers

### 1.5 In-Band Key Management System

#### Key Management Blocks
- [ ] Add new block types to BlockType enum
  - [ ] KeyManager = 10 (stores encrypted keys)
  - [ ] KeyExchange = 11 (unlock methods)
- [ ] Create `KeyManagerContent` block type
  - [ ] List of encrypted key entries
  - [ ] Version tracking with previous block reference
  - [ ] Salt for key derivation
  - [ ] Encrypted with master key
- [ ] Create `EncryptedKeyEntry` model
  - [ ] KeyId, Purpose, Algorithm fields
  - [ ] Encrypted key data
  - [ ] Creation/revocation timestamps
  - [ ] Extensible metadata dictionary

#### Key Exchange System
- [ ] Create `KeyExchangeContent` block type
  - [ ] Method identifier (password, webauthn, pgp, etc.)
  - [ ] Encrypted master key
  - [ ] Method-specific data storage
  - [ ] Active/inactive status
- [ ] Implement key exchange providers
  - [ ] `IKeyExchangeProvider` interface
  - [ ] `PasswordKeyExchangeProvider` with Argon2id/scrypt/pbkdf2
  - [ ] `WebAuthnKeyExchangeProvider` for FIDO2 keys
  - [ ] `PGPKeyExchangeProvider` for PGP key encryption
  - [ ] `PKCS11KeyExchangeProvider` for smart cards
- [ ] Create method-specific data structures
  - [ ] PasswordKeyExchange with KDF parameters
  - [ ] WebAuthnKeyExchange with credential data
  - [ ] PGPKeyExchange with key fingerprint
  - [ ] PKCS11KeyExchange with token info

#### Key Manager Implementation
- [ ] Create `BlockKeyManager` class
  - [ ] Master key unlock via key exchange
  - [ ] Key creation with automatic versioning
  - [ ] Key rotation with re-encryption support
  - [ ] Key recovery from previous versions
  - [ ] In-memory key caching
- [ ] Implement key operations
  - [ ] `UnlockAsync()` - decrypt master key
  - [ ] `CreateKeyAsync()` - generate new data keys
  - [ ] `GetKeyAsync()` - retrieve decrypted keys
  - [ ] `RotateKeyAsync()` - key rotation workflow
  - [ ] `RecoverPreviousKeyManagerAsync()` - rollback support
- [ ] Add bootstrap process
  - [ ] Initial master key generation
  - [ ] First key exchange creation
  - [ ] Default encryption keys setup

#### Security Features
- [ ] Implement multi-layer encryption
  - [ ] User auth → KeyExchange blocks
  - [ ] Master key → KeyManager blocks
  - [ ] Data keys → Email/Folder blocks
- [ ] Add key security measures
  - [ ] Master key never stored plaintext
  - [ ] Automatic key rotation policies
  - [ ] Key revocation with timestamps
  - [ ] Audit trail in append-only blocks

### 1.6 Configuration System
- [ ] Create `CompressionConfig` class
  - [ ] Enable/disable compression
  - [ ] Default algorithm selection
  - [ ] Minimum size threshold
  - [ ] Per-block-type overrides
- [ ] Create `EncryptionConfig` class
  - [ ] Enable/disable encryption
  - [ ] Default algorithm selection
  - [ ] Block type encryption policies
  - [ ] Default key selection
- [ ] Update RawBlockManager integration
  - [ ] Support compression/encryption parameters
  - [ ] Automatic algorithm selection
  - [ ] Extended header writing
  - [ ] Transparent decompression/decryption

## Phase 2: Manager Layer Implementation

### 2.1 FolderManager Enhancement
- [ ] Refactor FolderManager to use block storage
  - [ ] Implement `StoreFolderBlockAsync(FolderContent folder)`
  - [ ] Implement `StoreEnvelopeBlockAsync(FolderEnvelopeBlock envelopes)`
  - [ ] Add folder versioning logic
  - [ ] Track superseded folder blocks
- [ ] Update folder operations
  - [ ] `CreateFolderAsync()` - create new folder block
  - [ ] `AddEmailToFolderAsync()` - update envelope and folder blocks
  - [ ] `RemoveEmailFromFolderAsync()` - create new version without email
  - [ ] `GetFolderListingAsync()` - load envelope block for display
- [ ] Add cleanup support
  - [ ] Track superseded blocks in metadata
  - [ ] Implement `GetSupersededBlocksAsync()`

### 2.2 Create EmailManager
- [ ] Create `EmailDB.Format/FileManagement/EmailManager.cs`
  - [ ] Define high-level API matching current EmailDatabase
  - [ ] Coordinate between HybridEmailStore and manager stack
  - [ ] Implement transaction-like semantics for multi-block updates
- [ ] Implement core methods
  - [ ] `ImportEMLAsync()` - store email, update folder, create envelope
  - [ ] `SearchAsync()` - use ZoneTree indexes to find emails
  - [ ] `GetFolderListingAsync()` - retrieve envelopes efficiently
  - [ ] `MoveEmailAsync()` - update folder blocks atomically
  - [ ] `DeleteEmailAsync()` - mark as deleted, cleanup later

### 2.3 Update HybridEmailStore
- [ ] Refactor to use FolderManager for folder operations
  - [ ] Remove direct folder storage in ZoneTree
  - [ ] Update all indexes to store BlockLocation instead of data
- [ ] Add envelope support
  - [ ] Integrate envelope block creation on email import
  - [ ] Update folder operations to maintain envelopes
- [ ] Implement atomic multi-block updates
  - [ ] Create transaction-like wrapper for related updates
  - [ ] Ensure consistency between folder, envelope, and email blocks

## Phase 3: Index Management

### 3.1 ZoneTree Index Refactoring
- [ ] Update all indexes to store block references only
  - [ ] MessageId → CompoundKey (BlockId:LocalId)
  - [ ] EnvelopeHash → CompoundKey (for deduplication)
  - [ ] ContentHash → CompoundKey (for verification)
  - [ ] FolderPath → FolderBlockLocation
  - [ ] SearchTerm → List<CompoundKey>
- [ ] Add new indexes
  - [ ] EmailId → EnvelopeBlockLocation (for quick metadata access)
  - [ ] FolderPath → EnvelopeBlockLocation (direct envelope access)
- [ ] Implement index rebuilding
  - [ ] Scan all blocks to rebuild indexes
  - [ ] Verify index consistency

### 3.2 Search Optimization
- [ ] Update full-text search to work with new structure
  - [ ] Index email content during import
  - [ ] Maintain search terms → EmailId mapping
  - [ ] Optimize search result retrieval using envelopes
- [ ] Add search result preview using envelopes
  - [ ] Load envelopes for search results
  - [ ] Avoid loading full email data for previews

## Phase 4: Maintenance and Cleanup

### 4.1 MaintenanceManager Implementation
- [ ] Create `EmailDB.Format/FileManagement/MaintenanceManager.cs`
  - [ ] Implement background cleanup service
  - [ ] Track superseded blocks across all types
  - [ ] Safe deletion after verification
- [ ] Implement cleanup operations
  - [ ] `IdentifySupersededBlocksAsync()`
  - [ ] `VerifyBlockNotReferencedAsync(BlockId)`
  - [ ] `CompactDatabaseAsync()` - remove deleted blocks
  - [ ] `RebuildIndexesAsync()` - full index reconstruction
- [ ] Add safety checks
  - [ ] Verify block not referenced before deletion
  - [ ] Create backup before compaction
  - [ ] Transaction log for recovery

### 4.2 Version Management
- [ ] Implement version tracking for all mutable blocks
  - [ ] Folder blocks versioning
  - [ ] Envelope blocks versioning
  - [ ] Metadata blocks versioning
- [ ] Add version conflict resolution
  - [ ] Last-write-wins for single-writer model
  - [ ] Version comparison utilities
- [ ] Cleanup old versions
  - [ ] Configurable retention period
  - [ ] Keep N versions for recovery

## Phase 5: Format Versioning

### 5.1 Version Management Framework
- [ ] Create format version system
  - [ ] Major.Minor.Patch versioning scheme
  - [ ] Feature capabilities flags
  - [ ] Version negotiation on database open
- [ ] Implement upgrade paths
  - [ ] In-place upgrades for minor versions
  - [ ] Background migration for major versions
  - [ ] Compatibility matrix documentation
- [ ] Add version checks
  - [ ] Validate block format versions
  - [ ] Handle version mismatches gracefully
  - [ ] Provide clear upgrade messages

## Phase 6: Testing

### 6.1 Unit Tests
- [ ] Block type serialization tests
  - [ ] Test all payload encodings
  - [ ] Verify round-trip serialization
  - [ ] Test versioning
  - [ ] Test new block types (KeyManager, KeyExchange, Envelope)
- [ ] Compression tests
  - [ ] Test each compression algorithm
  - [ ] Verify compression/decompression round-trip
  - [ ] Test compression thresholds
  - [ ] Benchmark compression ratios
- [ ] Encryption tests
  - [ ] Test each encryption algorithm
  - [ ] Verify encryption/decryption round-trip
  - [ ] Test key management operations
  - [ ] Test authentication tag verification
- [ ] Key management tests
  - [ ] Test master key unlock with each method
  - [ ] Test key creation and rotation
  - [ ] Test key recovery scenarios
  - [ ] Test multiple key exchange methods
- [ ] Manager layer tests
  - [ ] FolderManager block operations
  - [ ] EmailManager coordination
  - [ ] MaintenanceManager cleanup
  - [ ] BlockKeyManager operations
- [ ] Index consistency tests
  - [ ] Verify index updates
  - [ ] Test index rebuilding
  - [ ] Test persistent index loading

### 6.2 Integration Tests
- [ ] End-to-end email import tests
  - [ ] Import → Store → Index → Search → Retrieve
  - [ ] Folder operations
  - [ ] Envelope generation and retrieval
- [ ] Performance benchmarks
  - [ ] Folder listing performance
  - [ ] Search performance
  - [ ] Import throughput
- [ ] Cleanup and maintenance tests
  - [ ] Superseded block cleanup
  - [ ] Database compaction
  - [ ] Recovery scenarios

### 6.3 Email Storage Tests
- [ ] Test email batching
  - [ ] Verify correct block sizing
  - [ ] Test soft limits behavior
  - [ ] Ensure no email splitting
- [ ] Test compound key system
  - [ ] Verify BlockId:LocalId lookups
  - [ ] Test collision prevention
  - [ ] Benchmark lookup performance
- [ ] Test adaptive sizing
  - [ ] Verify size progression
  - [ ] Test database growth handling
  - [ ] Measure block count at various scales

## Phase 7: Documentation

### 7.1 Architecture Documentation
- [ ] Update architecture diagrams
  - [ ] Show envelope block layer
  - [ ] Update data flow diagrams
  - [ ] Document index structure
- [ ] Update API documentation
  - [ ] EmailManager API reference
  - [ ] Migration guide
  - [ ] Performance characteristics

### 7.2 Code Examples
- [ ] Update console application
  - [ ] Use new EmailManager API
  - [ ] Show folder listing examples
  - [ ] Demonstrate search functionality
- [ ] Create migration examples
  - [ ] Step-by-step migration guide
  - [ ] Troubleshooting guide

## Phase 8: Performance Optimization

### 8.1 Caching Strategy
- [ ] Implement multi-layer caching
  - [ ] Envelope cache for folder listings (highest priority)
  - [ ] Block cache for recently accessed email blocks
  - [ ] Key cache for decrypted encryption keys
  - [ ] Index cache (leverage ZoneTree's built-in caching)
- [ ] Smart eviction policies
  - [ ] LRU for general caches
  - [ ] Pin hot folders in envelope cache
  - [ ] Predictive prefetching based on access patterns
- [ ] Memory management
  - [ ] Configure cache size limits
  - [ ] Monitor memory pressure
  - [ ] Adaptive cache sizing

### 8.2 Index Optimization
- [ ] Analyze index access patterns
- [ ] Optimize ZoneTree configuration
- [ ] Consider index partitioning for scale

## Completion Checklist
- [ ] All tests passing
- [ ] Performance benchmarks meet targets
- [ ] Documentation complete
- [ ] Migration tools tested
- [ ] Console application updated
- [ ] Code review completed
- [ ] Deprecation notices added