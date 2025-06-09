# EmailDB Solution Structure

## Current Projects (After Cleanup)

### 1. EmailDB.Format (Core Library)
The main implementation containing:
- **FileManagement/** - Core managers (RawBlockManager, CacheManager, FolderManager, etc.)
  - `AppendOnlyBlockStore.cs` - New efficient append-only storage
  - `HybridEmailStore.cs` - Combines append-only storage with ZoneTree indexes
  - `HashChainManager.cs` - Cryptographic hash chain for archival
  - `ArchiveManager.cs` - Read-only access to archived databases
  - `CheckpointManager.cs` - Backup and recovery system
- **Models/** - Data models and block types
- **Helpers/** - Utility classes
- **ZoneTree/** - ZoneTree integration components

### 2. EmailDB.UnitTests (All Tests)
Comprehensive test suite containing:
- **New Architecture Tests:**
  - `HybridEmailStorePerformanceTest.cs` - Performance benchmarks
  - `HybridStoreFolderSearchTest.cs` - Folder indexing tests
  - `AppendOnlyVsZoneTreeTest.cs` - Storage comparison
  - `HashChainArchiveE2ETest.cs` - Archive integrity tests
- **Stress & Reliability Tests:**
  - `ConcurrentAccessStressTest.cs` - Concurrency testing
  - `CorruptionRecoveryTest.cs` - Corruption recovery
  - `LargeDatasetEnduranceTest.cs` - Large-scale testing
  - `RealWorldScenarioTest.cs` - Real usage patterns
- **Analysis Tests:**
  - `DirectAnalysisTest.cs` - Storage analysis
  - `StorageOverheadAnalysisTest.cs` - Overhead measurement
  - `BatchingStorageAnalysisTest.cs` - Batching strategies
- **Core Tests:**
  - `RawBlockManagerBasicTest.cs` - Basic block operations
  - `CoreManagersIntegrationTest.cs` - Manager integration
  - Various other unit and integration tests

## Removed Projects
The following projects were removed as they were outdated or redundant:
- EmailDB.Testing - Used obsolete classes
- EmailDB.Testing.FileFormatBenchmark - Tested old implementations
- EmailDB.Format.CapnProto - Alternative serialization (not maintained)
- EmailDB.Format.Protobuf - Alternative serialization (not maintained)
- EmailDB.Testing.RawBlocks - Merged into UnitTests
- NetNinja.Testing.BlockManager - Merged into UnitTests

## Key Implementation Details

### Current Architecture
The project now uses a hybrid approach:
1. **Append-only block storage** for email data (99.6% storage efficiency)
2. **ZoneTree indexes** for fast searching and folder navigation
3. **Hash chains** for cryptographic integrity in archives
4. **Checkpoint system** for backup and recovery

### Testing Strategy
All tests are now consolidated in EmailDB.UnitTests:
- Unit tests for individual components
- Integration tests for manager interactions
- Performance tests with benchmarks
- Stress tests for reliability
- Real-world scenario simulations

## Building and Running

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Run specific test category
dotnet test --filter "FullyQualifiedName~HybridEmailStore"
```

## Notes
- Some older tests may need updating to match current APIs
- The main EmailDB.Format project builds successfully
- Focus development on the hybrid storage approach which provides the best balance of efficiency and functionality