# EmailDB Testing Framework

## Overview

This document defines a comprehensive testing framework for EmailDB that ensures reliability, performance, and correctness at every layer of the system, from low-level file format operations to high-level email storage and retrieval.

## Testing Philosophy

1. **Test at Every Layer**: Each component should have dedicated unit tests
2. **Fail Fast**: Tests should catch issues early in development
3. **Deterministic**: Tests must be repeatable and consistent
4. **Isolated**: Tests should not depend on external state
5. **Performance-Aware**: Track and prevent performance regressions

## Testing Layers

### Layer 1: File Format Tests
Tests the fundamental block storage format and operations.

```csharp
[Collection("FileFormat")]
public class BlockFormatTests : BaseEmailDBTest
{
    [Fact]
    public void Should_SerializeAndDeserialize_BlockHeader()
    {
        // Test 57-byte header format
    }
    
    [Theory]
    [InlineData(CompressionType.None)]
    [InlineData(CompressionType.LZ4)]
    [InlineData(CompressionType.Zstd)]
    public void Should_CompressAndDecompress_BlockPayload(CompressionType compression)
    {
        // Test all 127 compression algorithms
    }
    
    [Fact]
    public void Should_ValidateChecksum_OnCorruptedBlock()
    {
        // Test integrity validation
    }
}
```

### Layer 2: Manager Component Tests
Test each manager in isolation with mocked dependencies.

```csharp
public class RawBlockManagerTests : BaseEmailDBTest
{
    private readonly Mock<IFileSystem> _fileSystemMock;
    
    [Fact]
    public async Task Should_WriteBlock_WithCorrectFormat()
    {
        // Test block writing with validation
    }
    
    [Fact]
    public async Task Should_ReadBlock_WithIntegrityCheck()
    {
        // Test block reading with checksum verification
    }
}

public class MetadataManagerTests : BaseEmailDBTest
{
    [Fact]
    public async Task Should_PersistMetadata_InBlocks()
    {
        // Test metadata storage in append-only blocks
    }
}
```

### Layer 3: Integration Tests
Test interactions between multiple components.

```csharp
[Collection("Integration")]
public class EmailStorageIntegrationTests : BaseEmailDBTest
{
    [Fact]
    public async Task Should_StoreAndRetrieve_SingleEmail()
    {
        // End-to-end test with real components
    }
    
    [Fact]
    public async Task Should_BatchEmails_IntoOptimalBlocks()
    {
        // Test adaptive batching (50MB-1GB)
    }
    
    [Fact]
    public async Task Should_MaintainHashChain_AcrossBlocks()
    {
        // Test cryptographic integrity
    }
}
```

### Layer 4: Performance Tests
Automated performance benchmarks with regression detection.

```csharp
[Collection("Performance")]
public class PerformanceBenchmarks : BasePerformanceTest
{
    [Benchmark]
    public async Task WritePerformance_50MB_Sequential()
    {
        // Target: 50+ MB/s sustained
        AssertPerformance(throughput => throughput >= 50_000_000);
    }
    
    [Benchmark]
    public async Task ReadLatency_IndexedLookup()
    {
        // Target: < 0.1ms
        AssertLatency(latency => latency < TimeSpan.FromMilliseconds(0.1));
    }
    
    [Benchmark]
    public async Task SearchSpeed_50K_QueriesPerSecond()
    {
        // Target: 50,000+ queries/second
        AssertThroughput(qps => qps >= 50_000);
    }
}
```

### Layer 5: Stress Tests
Test system behavior under extreme conditions.

```csharp
[Collection("Stress")]
public class StressTests : BaseStressTest
{
    [Fact]
    public async Task Should_HandleConcurrentWrites_WithoutCorruption()
    {
        // 100 concurrent writers
        await RunConcurrentTest(writers: 100, readers: 0);
    }
    
    [Fact]
    public async Task Should_RecoverFromCrash_DuringWrite()
    {
        // Simulate crash scenarios
        await SimulateCrashDuringOperation();
    }
    
    [Fact]
    public async Task Should_HandleLargeDatasets_10TB()
    {
        // Test with 10TB of email data
        await TestLargeDataset(sizeInGB: 10_000);
    }
}
```

## Test Infrastructure

### Base Test Classes

```csharp
public abstract class BaseEmailDBTest : IDisposable
{
    protected string TestDbPath { get; }
    protected ITestOutputHelper Output { get; }
    protected TestDataFactory DataFactory { get; }
    
    protected BaseEmailDBTest(ITestOutputHelper output)
    {
        Output = output;
        TestDbPath = Path.Combine(Path.GetTempPath(), $"emaildb_test_{Guid.NewGuid()}");
        DataFactory = new TestDataFactory();
    }
    
    protected EmailDatabase CreateTestDatabase(DatabaseOptions options = null)
    {
        options ??= new DatabaseOptions { EnableCaching = true };
        return new EmailDatabase(TestDbPath, options);
    }
    
    public virtual void Dispose()
    {
        if (Directory.Exists(TestDbPath))
        {
            Directory.Delete(TestDbPath, recursive: true);
        }
    }
}

public abstract class BasePerformanceTest : BaseEmailDBTest
{
    protected Stopwatch Stopwatch { get; } = new();
    
    protected void AssertPerformance(Func<double, bool> assertion, string metric)
    {
        if (!assertion(GetMetricValue()))
        {
            throw new PerformanceException($"Performance assertion failed for {metric}");
        }
    }
}
```

### Test Data Factory

```csharp
public class TestDataFactory
{
    private readonly Faker _faker = new();
    
    public MimeMessage CreateEmail(EmailOptions options = null)
    {
        options ??= new EmailOptions();
        var message = new MimeMessage();
        
        message.From.Add(new MailboxAddress(_faker.Name.FullName(), _faker.Internet.Email()));
        message.To.Add(new MailboxAddress(_faker.Name.FullName(), _faker.Internet.Email()));
        message.Subject = options.Subject ?? _faker.Lorem.Sentence();
        
        var builder = new BodyBuilder
        {
            TextBody = options.Body ?? _faker.Lorem.Paragraphs(3),
            HtmlBody = options.HtmlBody ?? $"<html><body>{_faker.Lorem.Paragraphs(3)}</body></html>"
        };
        
        if (options.AttachmentCount > 0)
        {
            for (int i = 0; i < options.AttachmentCount; i++)
            {
                builder.Attachments.Add(CreateAttachment(options.AttachmentSize));
            }
        }
        
        message.Body = builder.ToMessageBody();
        return message;
    }
    
    public byte[] CreateEmlContent(int approximateSize = 1024)
    {
        var email = CreateEmail(new EmailOptions 
        { 
            AttachmentCount = approximateSize > 10240 ? 1 : 0,
            AttachmentSize = approximateSize / 2
        });
        
        using var stream = new MemoryStream();
        email.WriteTo(stream);
        return stream.ToArray();
    }
    
    public List<MimeMessage> CreateEmailBatch(int count, BatchOptions options = null)
    {
        return Enumerable.Range(0, count)
            .Select(_ => CreateEmail(options?.EmailOptions))
            .ToList();
    }
    
    public Block CreateTestBlock(BlockType type = BlockType.Data, int payloadSize = 1024)
    {
        return new Block
        {
            Header = new BlockHeader
            {
                Version = 1,
                Type = type,
                BlockId = (ulong)_faker.Random.Long(1, long.MaxValue),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            },
            Payload = _faker.Random.Bytes(payloadSize)
        };
    }
}
```

### Mock Factories

```csharp
public static class MockFactory
{
    public static Mock<IRawBlockManager> CreateRawBlockManager()
    {
        var mock = new Mock<IRawBlockManager>();
        var blocks = new Dictionary<ulong, Block>();
        
        mock.Setup(m => m.WriteBlockAsync(It.IsAny<Block>()))
            .ReturnsAsync((Block b) =>
            {
                blocks[b.Header.BlockId] = b;
                return Result<ulong>.Success(b.Header.BlockId);
            });
            
        mock.Setup(m => m.ReadBlockAsync(It.IsAny<ulong>()))
            .ReturnsAsync((ulong id) =>
            {
                return blocks.TryGetValue(id, out var block)
                    ? Result<Block>.Success(block)
                    : Result<Block>.Failure("Block not found");
            });
            
        return mock;
    }
    
    public static Mock<IBlockManager> CreateBlockManager()
    {
        var mock = new Mock<IBlockManager>();
        // Configure block manager mock
        return mock;
    }
    
    public static Mock<ICacheManager> CreateCacheManager()
    {
        var mock = new Mock<ICacheManager>();
        var cache = new Dictionary<string, object>();
        
        mock.Setup(m => m.GetAsync<It.IsAnyType>(It.IsAny<string>()))
            .ReturnsAsync((string key) =>
            {
                return cache.TryGetValue(key, out var value)
                    ? Result<object>.Success(value)
                    : Result<object>.Failure("Not in cache");
            });
            
        return mock;
    }
}
```

### Custom Assertions

```csharp
public static class EmailDBAssert
{
    public static void BlocksAreEqual(Block expected, Block actual)
    {
        Assert.Equal(expected.Header.BlockId, actual.Header.BlockId);
        Assert.Equal(expected.Header.Type, actual.Header.Type);
        Assert.Equal(expected.Header.Checksum, actual.Header.Checksum);
        Assert.Equal(expected.Payload, actual.Payload);
    }
    
    public static void EmailsAreEqual(MimeMessage expected, MimeMessage actual)
    {
        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.From.ToString(), actual.From.ToString());
        Assert.Equal(expected.To.ToString(), actual.To.ToString());
        // Compare body content
    }
    
    public static void HashChainIsValid(IEnumerable<Block> blocks)
    {
        Block previousBlock = null;
        foreach (var block in blocks)
        {
            if (previousBlock != null)
            {
                Assert.Equal(previousBlock.Header.BlockId, block.Header.PreviousBlockId);
                // Verify hash chain
            }
            previousBlock = block;
        }
    }
    
    public static void PerformanceWithinThreshold(TimeSpan actual, TimeSpan threshold)
    {
        Assert.True(actual <= threshold, 
            $"Performance {actual.TotalMilliseconds}ms exceeded threshold {threshold.TotalMilliseconds}ms");
    }
}
```

## Test Categories and Organization

### Directory Structure
```
EmailDB.UnitTests/
├── Unit/
│   ├── Core/
│   │   ├── BlockFormatTests.cs
│   │   ├── ChecksumTests.cs
│   │   └── CompressionTests.cs
│   ├── Managers/
│   │   ├── RawBlockManagerTests.cs
│   │   ├── BlockManagerTests.cs
│   │   ├── CacheManagerTests.cs
│   │   ├── MetadataManagerTests.cs
│   │   ├── FolderManagerTests.cs
│   │   └── EmailManagerTests.cs
│   └── Models/
│       ├── BlockTests.cs
│       ├── EmailEnvelopeTests.cs
│       └── FolderTests.cs
├── Integration/
│   ├── EmailStorageTests.cs
│   ├── SearchTests.cs
│   ├── BatchingTests.cs
│   └── RecoveryTests.cs
├── Performance/
│   ├── WriteBenchmarks.cs
│   ├── ReadBenchmarks.cs
│   ├── SearchBenchmarks.cs
│   └── MemoryBenchmarks.cs
├── Stress/
│   ├── ConcurrencyTests.cs
│   ├── LargeDatasetTests.cs
│   ├── CrashRecoveryTests.cs
│   └── EnduranceTests.cs
├── Infrastructure/
│   ├── BaseEmailDBTest.cs
│   ├── TestDataFactory.cs
│   ├── MockFactory.cs
│   └── EmailDBAssert.cs
└── Fixtures/
    ├── TestEmails/
    ├── TestData.json
    └── PerformanceBaselines.json
```

### Test Execution Strategy

#### CI/CD Pipeline
```yaml
# Quick Tests (< 1 minute)
- run: dotnet test --filter "Category=Unit" --no-build

# Integration Tests (< 5 minutes)  
- run: dotnet test --filter "Category=Integration" --no-build

# Performance Tests (< 10 minutes)
- run: dotnet test --filter "Category=Performance" --no-build
  # Compare against baselines in Fixtures/PerformanceBaselines.json

# Nightly Stress Tests (1-2 hours)
- run: dotnet test --filter "Category=Stress" --no-build
```

#### Local Development
```bash
# Run all unit tests for a specific manager
dotnet test --filter "FullyQualifiedName~RawBlockManager"

# Run performance tests with detailed output
dotnet test --filter "Category=Performance" --logger "console;verbosity=detailed"

# Run specific test method
dotnet test --filter "FullyQualifiedName~Should_WriteBlock_WithCorrectFormat"
```

## Test Coverage Requirements

### Minimum Coverage Targets
- **Unit Tests**: 90% code coverage
- **Integration Tests**: All public APIs tested
- **Performance Tests**: All critical paths benchmarked
- **Stress Tests**: All failure modes tested

### Critical Test Scenarios

1. **Block Storage**
   - Write/read with all compression types
   - Corruption detection and recovery
   - Hash chain validation
   - Concurrent access

2. **Email Operations**
   - Store/retrieve single emails
   - Batch processing (50MB-1GB blocks)
   - Compound ID (BlockId:LocalId) resolution
   - Attachment handling

3. **Search and Indexing**
   - Full-text search accuracy
   - Index performance (50K+ qps)
   - Complex query execution
   - Index rebuilding

4. **Data Integrity**
   - Cryptographic verification
   - Crash recovery
   - Backup/restore
   - Migration between versions

5. **Performance**
   - 50+ MB/s write throughput
   - < 0.1ms read latency
   - 99.6% storage efficiency
   - Memory usage under load

## Seeded Random Testing

### Overview
Seeded random testing ensures reproducible test scenarios while exploring edge cases and unexpected interactions. All random tests use a seed value that can be specified to reproduce failures.

### Random Testing Infrastructure

#### SeededRandomTestBase
Base class for all random tests providing reproducible randomness:

```csharp
public abstract class SeededRandomTestBase : BaseEmailDBTest
{
    protected Random Random { get; private set; }
    protected int Seed { get; private set; }
    
    protected SeededRandomTestBase(ITestOutputHelper output, int? seed = null) : base(output)
    {
        // Use provided seed or generate one based on current time
        Seed = seed ?? (int)DateTime.UtcNow.Ticks;
        Random = new Random(Seed);
        
        Output.WriteLine($"=== RANDOM TEST SEED: {Seed} ===");
        Output.WriteLine($"To reproduce this test, use seed: {Seed}");
    }
}
```

### Random Test Layers

#### Layer 1: Block-Level Random Testing
```csharp
[Collection("Random")]
public class RandomBlockTests : SeededRandomTestBase
{
    [Fact]
    public async Task Should_HandleRandomBlockSequence()
    {
        var scenarios = new RandomTestScenarios.BlockLayerScenarios(Seed);
        var blocks = scenarios.GenerateRandomBlockSequence(100);
        
        // Test with random compression types (0-127)
        // Test with random encryption types (0-127)
        // Test with random payload sizes
        // Verify hash chain integrity
    }
    
    [Fact]
    public async Task Should_DetectRandomCorruption()
    {
        var scenarios = new RandomTestScenarios.BlockLayerScenarios(Seed);
        var block = scenarios.GenerateRandomBlock();
        var corruptions = scenarios.GenerateCorruptionScenarios(block, 10);
        
        // Verify each corruption is detected
    }
}
```

#### Layer 2: Manager-Level Random Testing
```csharp
public class RandomManagerTests : SeededRandomTestBase
{
    [Fact]
    public async Task Should_HandleConcurrentRandomOperations()
    {
        var scenarios = new RandomTestScenarios.ManagerLayerScenarios(Seed);
        var operations = scenarios.GenerateConcurrentOperations(
            threadCount: 10, 
            operationsPerThread: 50
        );
        
        // Execute operations concurrently
        // Verify data consistency
    }
}
```

#### Layer 3: End-to-End Random Testing
```csharp
public class RandomE2ETests : SeededRandomTestBase
{
    [Fact]
    public async Task Should_HandleRandomEmailOperations()
    {
        var generator = new RandomOperationGenerator(Seed);
        var operations = generator.GenerateOperationSequence(1000);
        
        using var database = CreateTestDatabase();
        var executor = new RandomOperationExecutor(database);
        
        foreach (var operation in operations)
        {
            RecordRandomOperation(operation.Description);
            var result = await executor.ExecuteAsync(operation);
            
            Assert.True(result.Success, 
                $"Operation failed: {operation.Description}\n" +
                $"Error: {result.Error}\n" +
                $"Seed: {Seed}");
        }
        
        // Verify final state consistency
        await VerifyDatabaseConsistency(database);
    }
}
```

### Random Operation Generator

The `RandomOperationGenerator` creates realistic operation sequences:

```csharp
public class RandomOperationGenerator
{
    // Weighted operation selection
    var operationType = _random.WeightedChoice(
        new WeightedChoice<EmailOperationType>(EmailOperationType.AddEmail, 40.0),
        new WeightedChoice<EmailOperationType>(EmailOperationType.DeleteEmail, 10.0),
        new WeightedChoice<EmailOperationType>(EmailOperationType.MoveEmail, 15.0),
        new WeightedChoice<EmailOperationType>(EmailOperationType.CreateFolder, 5.0),
        new WeightedChoice<EmailOperationType>(EmailOperationType.SearchEmails, 20.0),
        new WeightedChoice<EmailOperationType>(EmailOperationType.BatchAddEmails, 5.0)
    );
}
```

### Random Test Scenarios

#### Email Scenarios
- Random email sizes: Small (<10KB), Medium (10KB-1MB), Large (1MB-10MB), Huge (>10MB)
- Random attachment counts: 0-10 attachments
- Random recipient counts: 1-50 recipients
- Random folder depths: 0-5 levels
- Random email flags combinations

#### Search Scenarios
- Simple queries: Single field searches
- Medium queries: Combined field searches with date ranges
- Complex queries: Boolean operators, negation, wildcards

#### Stress Scenarios
- Concurrent users: 10-500 simultaneous connections
- Operation mix: Configurable read/write/delete/search percentages
- Failure injection: Network timeouts, disk full, process crashes
- Resource limits: Memory, disk, CPU constraints

### Reproducing Random Test Failures

When a random test fails, the seed is included in the output:

```
=== RANDOM TEST SEED: 1234567890 ===
To reproduce this test, use seed: 1234567890
================================

Test Failed: Should_HandleRandomEmailOperations
Operation failed: Move email from folder 3 to 7
Error: Folder not found
Seed: 1234567890
```

To reproduce:
```csharp
[Fact]
public async Task Reproduce_SpecificFailure()
{
    // Use the exact seed from the failure
    var seed = 1234567890;
    var test = new RandomE2ETests(Output, seed);
    await test.Should_HandleRandomEmailOperations();
}
```

### Random Test Configuration

```csharp
public class RandomOperationOptions
{
    // Operation weights (higher = more likely)
    public double AddEmailWeight { get; set; } = 40.0;
    public double DeleteEmailWeight { get; set; } = 10.0;
    public double MoveEmailWeight { get; set; } = 15.0;
    
    // Operation parameters
    public int MaxAttachments { get; set; } = 5;
    public int MinBatchSize { get; set; } = 10;
    public int MaxBatchSize { get; set; } = 100;
}
```

### CI/CD Integration

```yaml
# Random Tests (5-10 minutes)
- run: dotnet test --filter "Category=Random" --no-build
  # Runs with new random seed each time
  
# Nightly Extended Random Tests (30-60 minutes)
- run: dotnet test --filter "Category=RandomExtended" --no-build
  # Runs longer sequences with more operations
```

## Continuous Improvement

### Performance Tracking
- Record performance metrics for each test run
- Alert on regressions > 10%
- Maintain historical baselines
- Profile memory and CPU usage

### Test Maintenance
- Review and update tests with each feature
- Remove obsolete tests
- Refactor duplicate test code
- Keep test data current
- Analyze random test failures for patterns

### Documentation
- Document complex test scenarios
- Explain performance targets
- Provide troubleshooting guides
- Share best practices
- Maintain seed database for interesting test cases

This comprehensive testing framework, including seeded random testing, ensures EmailDB maintains its performance targets and reliability guarantees throughout development and deployment.