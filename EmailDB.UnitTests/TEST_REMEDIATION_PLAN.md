# Test Remediation Plan

## Summary of Issues

After detailed analysis, the unit tests have fundamental architectural mismatches with the actual EmailDB.Format API:

1. **BlockContent** - Tests expect a simple class with properties, but actual is an abstract class
2. **MetadataContent** - Test expects different properties than actual implementation
3. **CacheManager** - Tests expect methods that don't exist (UpdateSegment, GetMetadataAsync, etc.)
4. **Missing Types** - CleanupContent, GetAllBlockLocations, etc. don't exist in actual API

## Recommended Approach

### Phase 1: Disable Broken Tests (Immediate)

Comment out or delete the following test files that have fundamental API mismatches:
- `EmailDBBaseFunctionalityTests.cs` - Uses wrong MetadataContent properties
- `EmailDBManagerIntegrationTests.cs` - Uses non-existent CacheManager methods
- `EmailDBCoreTests.cs` - Multiple API mismatches
- `EmailDBStressTests.cs` - Missing SegmentManager type

### Phase 2: Keep Working Tests

These tests work with minimal changes:
- `RawBlockManagerTests.cs` - Basic file I/O tests
- `StorageManagerTests.cs` - Self-contained test implementation
- `CacheManagerTests.cs` - Needs minor fix (already applied)
- `BlockManagerTests.cs` - Works with test helpers

### Phase 3: Create New Tests (Future)

Create new tests that properly use the actual API:
1. Study the actual EmailDB.Format implementation
2. Create tests that use the real BlockContent hierarchy
3. Test actual CacheManager functionality
4. Use proper serialization with iBlockContentSerializer

## Specific Fixes for Keeping Tests Working

### 1. Fix Ambiguous References
- Remove duplicate type definitions from TestModels.cs
- Use fully qualified names where needed
- Create test-specific types with different names

### 2. Fix Constructor Issues
```csharp
// Add serializer parameter
var serializer = new DefaultBlockContentSerializer();
var cacheManager = new CacheManager(rawBlockManager, serializer);
```

### 3. Create Test-Specific Implementations
Instead of trying to mock the complex actual types, create simplified test implementations that implement the necessary interfaces.

## Test Categories

### Keep As-Is (Working)
- Unit tests for basic file operations
- Tests using self-contained test implementations

### Modify (Quick Fixes)
- Tests with constructor parameter issues
- Tests with simple method name changes

### Remove/Rewrite (Major Issues)
- Tests expecting different object models
- Tests calling non-existent methods
- Tests with fundamental architectural mismatches

## Next Steps

1. **Immediate**: Comment out broken test files to get build working
2. **Short-term**: Fix remaining compilation issues in keeper tests
3. **Long-term**: Design new test suite that matches actual API