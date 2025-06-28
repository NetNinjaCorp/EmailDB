# EmailDB Implementation TODO

## Overview
Refactor EmailDB to use HybridEmailStore architecture with all data stored in append-only blocks, including folder metadata and email envelopes for efficient listings.

**Status**: Phases 1-5 COMPLETED ✅ (as of Stage 5 completion)

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

## Phase 1: Core Architecture Refactoring ✅ COMPLETED

### 1.1 Block Type Definitions
- [x] Create `EmailEnvelope` model in `Models/EmailContent/`
  - [x] Define fields: EmailId, MessageId, Subject, From, To, Date, Size, HasAttachments, Flags
  - [x] Add serialization support
- [x] Create `FolderEnvelopeBlock` in `Models/BlockTypes/`
  - [x] Define structure with FolderPath, Version, List<EmailEnvelope>
  - [x] Implement BlockContent interface
- [x] Update `FolderContent` in `Models/BlockTypes/`
  - [x] Add EnvelopeBlockId field
  - [x] Add Version field for superseding support
  - [x] Add LastModified timestamp
- [x] Create missing ZoneTree block types
  - [x] `ZoneTreeSegmentKVContent` for key-value storage
  - [x] `ZoneTreeSegmentVectorContent` for vector storage
  - [x] Implement proper serialization for both
- [x] Add persistent block index support
  - [x] Design index file format for fast block lookups
  - [x] Implement index persistence on block writes
  - [x] Add index loading on startup
  - [x] Create index rebuild capability

### 1.2 Email Storage and Batching
- [x] Implement Email ID system
  - [x] Create `EmailHashedID` with envelope and content hashes
  - [x] Include more fields to prevent collisions (MessageId, From, To, Date, Subject, Cc, InReplyTo, Size)
  - [x] Implement compound key system (BlockId:LocalId)
  - [x] Add duplicate detection using dual-hash approach
- [x] Create adaptive block sizing
  - [x] Implement `AdaptiveBlockSizer` with size progression:
    - [x] < 5GB: 50MB blocks
    - [x] < 25GB: 100MB blocks
    - [x] < 100GB: 250MB blocks
    - [x] < 500GB: 500MB blocks
    - [x] >= 500GB: 1GB blocks
  - [x] Soft limits - blocks can be slightly over/under target size
  - [x] No email splitting across blocks
- [x] Implement email batching system
  - [x] Create `EmailBlockBuilder` for accumulating emails
  - [x] Automatic flush when reaching target size
  - [x] Serialize multiple emails per block
  - [x] Track pending IDs until block is written
- [x] Update storage manager
  - [x] Coordinate block sizing based on database growth
  - [x] Handle email addition with deduplication checks
  - [x] Finalize email IDs after block write
  - [x] Update all relevant indexes atomically

### 1.3 Serialization Infrastructure
- [x] Use existing Protobuf serialization
  - [x] Ensure all new block types have Protobuf definitions
  - [x] Reuse existing binary walking functions
  - [x] Maintain compatibility with current block format
- [x] Update `DefaultBlockContentSerializer`
  - [x] Refactor to use `IPayloadEncoding` interface
  - [x] Remove dependency on old `iBlockContentSerializer`
  - [x] Ensure backward compatibility

### 1.4 Compression and Encryption Infrastructure

#### Block Format Enhancement
- [x] Update Block header to utilize Flags field (32 bits)
  - [x] Define BlockFlags enum with compression/encryption bits
  - [x] Support 127 compression algorithms (7 bits)
  - [x] Support 127 encryption algorithms (7 bits)
  - [x] Reserve remaining bits for future use
- [x] Create ExtendedBlockHeader for compressed/encrypted blocks
  - [x] UncompressedSize field for compressed blocks
  - [x] IV, AuthTag, and KeyId fields for encrypted blocks
  - [x] Variable length based on flags

#### Compression System
- [x] Create `ICompressionProvider` interface
  - [x] Define Compress/Decompress methods with Result<T> pattern
  - [x] Include algorithm ID and name properties
- [x] Implement compression providers
  - [x] `GzipCompressionProvider` (ID: 1)
  - [x] `LZ4CompressionProvider` (ID: 2)
  - [x] `ZstdCompressionProvider` (ID: 3)
  - [x] `BrotliCompressionProvider` (ID: 4)
- [x] Create `AlgorithmRegistry` for managing providers
  - [x] Registration system for compression algorithms
  - [x] Lookup by algorithm ID
  - [x] Extensible for future algorithms

#### Encryption System
- [x] Create `IEncryptionProvider` interface
  - [x] Define Encrypt/Decrypt methods with Result<T> pattern
  - [x] Include IV size, auth tag size properties
  - [x] Support key ID for multi-key scenarios
- [x] Implement encryption providers
  - [x] `AES256GcmEncryptionProvider` (ID: 1)
  - [x] `ChaCha20Poly1305EncryptionProvider` (ID: 2)
  - [x] `AES256CbcHmacEncryptionProvider` (ID: 3)
- [x] Create `BlockProcessor` for compression/encryption pipeline
  - [x] Compress then encrypt workflow
  - [x] Decrypt then decompress workflow
  - [x] Handle extended headers

### 1.5 In-Band Key Management System

#### Key Management Blocks
- [x] Add new block types to BlockType enum
  - [x] KeyManager = 11 (stores encrypted keys)
  - [x] KeyExchange = 12 (unlock methods)
- [x] Create `KeyManagerContent` block type
  - [x] List of encrypted key entries
  - [x] Version tracking with previous block reference
  - [x] Salt for key derivation
  - [x] Encrypted with master key
- [x] Create `EncryptedKeyEntry` model
  - [x] KeyId, Purpose, Algorithm fields
  - [x] Encrypted key data
  - [x] Creation/revocation timestamps
  - [x] Extensible metadata dictionary

#### Key Exchange System
- [x] Create `KeyExchangeContent` block type
  - [x] Method identifier (password, webauthn, pgp, etc.)
  - [x] Encrypted master key
  - [x] Method-specific data storage
  - [x] Active/inactive status
- [x] Implement key exchange providers
  - [x] `IKeyExchangeProvider` interface
  - [x] `PasswordKeyExchangeProvider` with Argon2id/scrypt/pbkdf2
  - [x] `WebAuthnKeyExchangeProvider` for FIDO2 keys
  - [x] `PGPKeyExchangeProvider` for PGP key encryption
  - [x] `PKCS11KeyExchangeProvider` for smart cards
- [x] Create method-specific data structures
  - [x] PasswordKeyExchange with KDF parameters
  - [x] WebAuthnKeyExchange with credential data
  - [x] PGPKeyExchange with key fingerprint
  - [x] PKCS11KeyExchange with token info

#### Key Manager Implementation
- [x] Create `EncryptionKeyManager` class
  - [x] Master key unlock via key exchange
  - [x] Key creation with automatic versioning
  - [x] Key rotation with re-encryption support
  - [x] Key recovery from previous versions
  - [x] In-memory key caching
- [x] Implement key operations
  - [x] `Unlock()` - decrypt master key
  - [x] `CreateKey()` - generate new data keys
  - [x] `GetKey()` - retrieve decrypted keys
  - [x] `RotateKey()` - key rotation workflow
  - [x] `ExportKeyManagerContent()` - export for storage
- [x] Add bootstrap process
  - [x] Initial master key generation
  - [x] First key exchange creation
  - [x] Default encryption keys setup

#### Security Features
- [x] Implement multi-layer encryption
  - [x] User auth → KeyExchange blocks
  - [x] Master key → KeyManager blocks
  - [x] Data keys → Email/Folder blocks
- [x] Add key security measures
  - [x] Master key never stored plaintext
  - [x] Automatic key rotation policies
  - [x] Key revocation with timestamps
  - [x] Audit trail in append-only blocks

### 1.6 Configuration System
- [x] Create `CompressionConfig` class
  - [x] Enable/disable compression
  - [x] Default algorithm selection
  - [x] Minimum size threshold
  - [x] Per-block-type overrides
- [x] Create `EncryptionConfig` class
  - [x] Enable/disable encryption
  - [x] Default algorithm selection
  - [x] Block type encryption policies
  - [x] Default key selection
- [x] Update RawBlockManager integration
  - [x] Support compression/encryption parameters
  - [x] Automatic algorithm selection
  - [x] Extended header writing
  - [x] Transparent decompression/decryption

## Phase 2: Manager Layer Implementation ✅ COMPLETED

### 2.1 FolderManager Enhancement
- [x] Refactor FolderManager to use block storage
  - [x] Implement `StoreFolderBlockAsync(FolderContent folder)`
  - [x] Implement `StoreEnvelopeBlockAsync(FolderEnvelopeBlock envelopes)`
  - [x] Add folder versioning logic
  - [x] Track superseded folder blocks
- [x] Update folder operations
  - [x] `CreateFolderAsync()` - create new folder block
  - [x] `AddEmailToFolderAsync()` - update envelope and folder blocks
  - [x] `RemoveEmailFromFolderAsync()` - create new version without email
  - [x] `GetFolderListingAsync()` - load envelope block for display
- [x] Add cleanup support
  - [x] Track superseded blocks in metadata
  - [x] Implement `GetSupersededBlocksAsync()`

### 2.2 Create EmailManager
- [x] Create `EmailDB.Format/FileManagement/EmailManager.cs`
  - [x] Define high-level API matching current EmailDatabase
  - [x] Coordinate between HybridEmailStore and manager stack
  - [x] Implement transaction-like semantics for multi-block updates
- [x] Implement core methods
  - [x] `ImportEMLAsync()` - store email, update folder, create envelope
  - [x] `SearchAsync()` - use ZoneTree indexes to find emails
  - [x] `GetFolderListingAsync()` - retrieve envelopes efficiently
  - [x] `MoveEmailAsync()` - update folder blocks atomically
  - [x] `DeleteEmailAsync()` - mark as deleted, cleanup later

### 2.3 Update HybridEmailStore
- [x] Refactor to use FolderManager for folder operations
  - [x] Remove direct folder storage in ZoneTree
  - [x] Update all indexes to store BlockLocation instead of data
- [x] Add envelope support
  - [x] Integrate envelope block creation on email import
  - [x] Update folder operations to maintain envelopes
- [x] Implement atomic multi-block updates
  - [x] Create transaction-like wrapper for related updates
  - [x] Ensure consistency between folder, envelope, and email blocks

## Phase 3: Index Management ✅ COMPLETED

### 3.1 ZoneTree Index Refactoring
- [x] Update all indexes to store block references only
  - [x] MessageId → CompoundKey (BlockId:LocalId)
  - [x] EnvelopeHash → CompoundKey (for deduplication)
  - [x] ContentHash → CompoundKey (for verification)
  - [x] FolderPath → FolderBlockLocation
  - [x] SearchTerm → List<CompoundKey>
- [x] Add new indexes
  - [x] EmailId → EnvelopeBlockLocation (for quick metadata access)
  - [x] FolderPath → EnvelopeBlockLocation (direct envelope access)
- [x] Implement index rebuilding
  - [x] Scan all blocks to rebuild indexes
  - [x] Verify index consistency

### 3.2 Search Optimization
- [x] Update full-text search to work with new structure
  - [x] Index email content during import
  - [x] Maintain search terms → EmailId mapping
  - [x] Optimize search result retrieval using envelopes
- [x] Add search result preview using envelopes
  - [x] Load envelopes for search results
  - [x] Avoid loading full email data for previews

## Phase 4: Maintenance and Cleanup ✅ COMPLETED

### 4.1 MaintenanceManager Implementation
- [x] Create `EmailDB.Format/FileManagement/MaintenanceManager.cs`
  - [x] Implement background cleanup service
  - [x] Track superseded blocks across all types
  - [x] Safe deletion after verification
- [x] Implement cleanup operations
  - [x] `IdentifySupersededBlocksAsync()`
  - [x] `VerifyBlockNotReferencedAsync(BlockId)`
  - [x] `CompactDatabaseAsync()` - remove deleted blocks
  - [x] `RebuildIndexesAsync()` - full index reconstruction
- [x] Add safety checks
  - [x] Verify block not referenced before deletion
  - [x] Create backup before compaction
  - [x] Transaction log for recovery

### 4.2 Version Management
- [x] Implement version tracking for all mutable blocks
  - [x] Folder blocks versioning
  - [x] Envelope blocks versioning
  - [x] Metadata blocks versioning
- [x] Add version conflict resolution
  - [x] Last-write-wins for single-writer model
  - [x] Version comparison utilities
- [x] Cleanup old versions
  - [x] Configurable retention period
  - [x] Keep N versions for recovery

## Phase 5: Format Versioning ✅ COMPLETED

### 5.1 Version Management Framework
- [x] Create format version system
  - [x] Major.Minor.Patch versioning scheme
  - [x] Feature capabilities flags
  - [x] Version negotiation on database open
- [x] Implement upgrade paths
  - [x] In-place upgrades for minor versions
  - [x] Background migration for major versions
  - [x] Compatibility matrix documentation
- [x] Add version checks
  - [x] Validate block format versions
  - [x] Handle version mismatches gracefully
  - [x] Provide clear upgrade messages

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