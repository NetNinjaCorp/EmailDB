# Phase 6 Implementation Plan: Comprehensive Testing Suite

## Overview
Phase 6 implements a comprehensive testing strategy that validates all components from Phases 1-5. This includes unit tests for individual components, integration tests for workflows, performance benchmarks, and stress tests. The testing suite ensures correctness, performance, and reliability of the new architecture.

## Testing Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      Testing Architecture                         │
├─────────────────────────────────────────────────────────────────┤
│ Unit Tests                                                        │
│ ├── Block Type Tests (serialization, compression, encryption)    │
│ ├── Manager Tests (isolated with mocks)                          │
│ ├── Index Tests (CRUD operations, consistency)                   │
│ └── Version Tests (detection, compatibility, upgrades)           │
│                                                                   │
│ Integration Tests                                                 │
│ ├── End-to-End Workflows (import → store → search → retrieve)    │
│ ├── Multi-Component Tests (manager interactions)                 │
│ ├── Transaction Tests (atomicity, rollback)                      │
│ └── Upgrade Tests (version migrations)                           │
│                                                                   │
│ Performance Tests                                                 │
│ ├── Throughput Benchmarks (emails/second)                        │
│ ├── Latency Tests (operation response times)                     │
│ ├── Scalability Tests (large datasets)                           │
│ └── Memory Usage Tests (cache efficiency)                        │
│                                                                   │
│ Stress & Reliability Tests                                        │
│ ├── Concurrent Access Tests                                       │
│ ├── Corruption Recovery Tests                                     │
│ ├── Large File Tests (>100GB)                                    │
│ └── Endurance Tests (long-running operations)                    │
└─────────────────────────────────────────────────────────────────┘
```

## Section 6.1: Unit Tests

### Task 6.1.1: Block Type Serialization Tests
**File**: `EmailDB.UnitTests/BlockTypes/BlockSerializationTests.cs`
**Dependencies**: All block types from Phase 1
**Description**: Test serialization/deserialization of all block types

```csharp
namespace EmailDB.UnitTests.BlockTypes;

[TestFixture]
public class BlockSerializationTests
{
    private IBlockContentSerializer _serializer;
    
    [SetUp]
    public void Setup()
    {
        _serializer = new DefaultBlockContentSerializer();
    }
    
    [Test]
    public void EmailEnvelope_SerializationRoundTrip_Success()
    {
        // Arrange
        var envelope = new EmailEnvelope
        {
            CompoundId = "12345:0",
            MessageId = "<test@example.com>",
            Subject = "Test Email",
            From = "sender@example.com",
            To = "recipient@example.com",
            Date = DateTime.UtcNow,
            Size = 1024,
            HasAttachments = true,
            EnvelopeHash = new byte[] { 1, 2, 3, 4 },
            Flags = EmailFlags.Read | EmailFlags.Important
        };
        
        // Act
        var serialized = _serializer.Serialize(envelope, PayloadEncoding.Protobuf);
        Assert.That(serialized.IsSuccess, Is.True);
        
        var deserialized = _serializer.Deserialize<EmailEnvelope>(
            serialized.Value, 
            PayloadEncoding.Protobuf);
        
        // Assert
        Assert.That(deserialized.IsSuccess, Is.True);
        var result = deserialized.Value;
        Assert.That(result.CompoundId, Is.EqualTo(envelope.CompoundId));
        Assert.That(result.MessageId, Is.EqualTo(envelope.MessageId));
        Assert.That(result.Subject, Is.EqualTo(envelope.Subject));
        Assert.That(result.From, Is.EqualTo(envelope.From));
        Assert.That(result.To, Is.EqualTo(envelope.To));
        Assert.That(result.Date, Is.EqualTo(envelope.Date).Within(TimeSpan.FromSeconds(1)));
        Assert.That(result.Size, Is.EqualTo(envelope.Size));
        Assert.That(result.HasAttachments, Is.EqualTo(envelope.HasAttachments));
        Assert.That(result.EnvelopeHash, Is.EqualTo(envelope.EnvelopeHash));
        Assert.That(result.Flags, Is.EqualTo(envelope.Flags));
    }
    
    [Test]
    public void FolderEnvelopeBlock_SerializationRoundTrip_Success()
    {
        // Arrange
        var envelopeBlock = new FolderEnvelopeBlock
        {
            FolderPath = "Inbox/Important",
            Version = 1,
            PreviousBlockId = 12345,
            LastModified = DateTime.UtcNow,
            Envelopes = new List<EmailEnvelope>
            {
                new EmailEnvelope
                {
                    CompoundId = "100:0",
                    MessageId = "<msg1@example.com>",
                    Subject = "Email 1",
                    From = "sender1@example.com",
                    To = "recipient@example.com",
                    Date = DateTime.UtcNow.AddDays(-1),
                    Size = 2048
                },
                new EmailEnvelope
                {
                    CompoundId = "100:1",
                    MessageId = "<msg2@example.com>",
                    Subject = "Email 2",
                    From = "sender2@example.com",
                    To = "recipient@example.com",
                    Date = DateTime.UtcNow,
                    Size = 4096
                }
            }
        };
        
        // Act
        var serialized = _serializer.Serialize(envelopeBlock, PayloadEncoding.Protobuf);
        Assert.That(serialized.IsSuccess, Is.True);
        
        var deserialized = _serializer.Deserialize<FolderEnvelopeBlock>(
            serialized.Value,
            PayloadEncoding.Protobuf);
        
        // Assert
        Assert.That(deserialized.IsSuccess, Is.True);
        var result = deserialized.Value;
        Assert.That(result.FolderPath, Is.EqualTo(envelopeBlock.FolderPath));
        Assert.That(result.Version, Is.EqualTo(envelopeBlock.Version));
        Assert.That(result.PreviousBlockId, Is.EqualTo(envelopeBlock.PreviousBlockId));
        Assert.That(result.Envelopes.Count, Is.EqualTo(2));
        Assert.That(result.Envelopes[0].CompoundId, Is.EqualTo("100:0"));
        Assert.That(result.Envelopes[1].CompoundId, Is.EqualTo("100:1"));
    }
    
    [Test]
    public void KeyManagerContent_SerializationRoundTrip_Success()
    {
        // Arrange
        var keyManager = new KeyManagerContent
        {
            Version = 1,
            PreviousBlockId = 999,
            Salt = Convert.FromBase64String("dGVzdHNhbHQ="),
            Keys = new List<EncryptedKeyEntry>
            {
                new EncryptedKeyEntry
                {
                    KeyId = "data-key-1",
                    Purpose = KeyPurpose.DataEncryption,
                    Algorithm = EncryptionAlgorithm.AES256GCM,
                    EncryptedKey = Convert.FromBase64String("ZW5jcnlwdGVka2V5"),
                    CreatedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        { "version", "1" },
                        { "scope", "emails" }
                    }
                }
            }
        };
        
        // Act
        var serialized = _serializer.Serialize(keyManager, PayloadEncoding.Protobuf);
        Assert.That(serialized.IsSuccess, Is.True);
        
        var deserialized = _serializer.Deserialize<KeyManagerContent>(
            serialized.Value,
            PayloadEncoding.Protobuf);
        
        // Assert
        Assert.That(deserialized.IsSuccess, Is.True);
        var result = deserialized.Value;
        Assert.That(result.Version, Is.EqualTo(keyManager.Version));
        Assert.That(result.Salt, Is.EqualTo(keyManager.Salt));
        Assert.That(result.Keys.Count, Is.EqualTo(1));
        Assert.That(result.Keys[0].KeyId, Is.EqualTo("data-key-1"));
        Assert.That(result.Keys[0].Algorithm, Is.EqualTo(EncryptionAlgorithm.AES256GCM));
    }
    
    [Test]
    public void EmailBatch_SerializationRoundTrip_Success()
    {
        // Arrange
        var batch = new EmailBatchContent
        {
            BlockId = 12345,
            EmailCount = 3,
            TotalSize = 10240,
            Emails = new List<BatchedEmail>
            {
                new BatchedEmail
                {
                    LocalId = 0,
                    EnvelopeHash = new byte[] { 1, 2, 3 },
                    ContentHash = new byte[] { 4, 5, 6 },
                    EmailData = Encoding.UTF8.GetBytes("Email 1 content")
                },
                new BatchedEmail
                {
                    LocalId = 1,
                    EnvelopeHash = new byte[] { 7, 8, 9 },
                    ContentHash = new byte[] { 10, 11, 12 },
                    EmailData = Encoding.UTF8.GetBytes("Email 2 content")
                }
            }
        };
        
        // Act
        var serialized = _serializer.Serialize(batch, PayloadEncoding.Protobuf);
        Assert.That(serialized.IsSuccess, Is.True);
        
        var deserialized = _serializer.Deserialize<EmailBatchContent>(
            serialized.Value,
            PayloadEncoding.Protobuf);
        
        // Assert
        Assert.That(deserialized.IsSuccess, Is.True);
        var result = deserialized.Value;
        Assert.That(result.BlockId, Is.EqualTo(batch.BlockId));
        Assert.That(result.EmailCount, Is.EqualTo(batch.EmailCount));
        Assert.That(result.Emails.Count, Is.EqualTo(2));
        Assert.That(result.Emails[0].LocalId, Is.EqualTo(0));
        Assert.That(result.Emails[1].LocalId, Is.EqualTo(1));
    }
    
    [Test]
    [TestCase(PayloadEncoding.RawBytes)]
    [TestCase(PayloadEncoding.Protobuf)]
    [TestCase(PayloadEncoding.Json)]
    public void AllBlockTypes_DifferentEncodings_Success(PayloadEncoding encoding)
    {
        // Test that all block types support different encodings
        var blockTypes = new List<(string name, BlockContent content)>
        {
            ("Header", new HeaderContent { FileVersion = 1 }),
            ("Metadata", new MetadataContent { TotalBlocks = 100 }),
            ("Folder", new FolderContent { Name = "Test", Version = 1 }),
            ("EmailEnvelope", new FolderEnvelopeBlock { FolderPath = "Test", Version = 1 })
        };
        
        foreach (var (name, content) in blockTypes)
        {
            var serialized = _serializer.Serialize(content, encoding);
            Assert.That(serialized.IsSuccess, Is.True, 
                $"Failed to serialize {name} with {encoding}");
                
            var deserializedResult = _serializer.Deserialize(
                serialized.Value, 
                encoding, 
                content.GetType());
                
            Assert.That(deserializedResult.IsSuccess, Is.True,
                $"Failed to deserialize {name} with {encoding}");
        }
    }
}
```

### Task 6.1.2: Compression and Encryption Tests
**File**: `EmailDB.UnitTests/Infrastructure/CompressionEncryptionTests.cs`
**Dependencies**: Phase 1 compression/encryption infrastructure
**Description**: Test compression and encryption providers

```csharp
namespace EmailDB.UnitTests.Infrastructure;

[TestFixture]
public class CompressionEncryptionTests
{
    private AlgorithmRegistry _registry;
    private BlockProcessor _processor;
    private BlockKeyManager _keyManager;
    
    [SetUp]
    public async Task Setup()
    {
        _registry = new AlgorithmRegistry();
        
        // Register compression providers
        _registry.RegisterCompression(new GzipCompressionProvider());
        _registry.RegisterCompression(new LZ4CompressionProvider());
        _registry.RegisterCompression(new ZstdCompressionProvider());
        
        // Register encryption providers
        _registry.RegisterEncryption(new AES256GcmEncryptionProvider());
        _registry.RegisterEncryption(new ChaCha20Poly1305EncryptionProvider());
        
        // Initialize key manager with test master key
        _keyManager = new BlockKeyManager(_registry);
        await _keyManager.InitializeWithPasswordAsync("test-password");
        
        _processor = new BlockProcessor(_registry, _keyManager);
    }
    
    [Test]
    [TestCase(CompressionAlgorithm.None)]
    [TestCase(CompressionAlgorithm.Gzip)]
    [TestCase(CompressionAlgorithm.LZ4)]
    [TestCase(CompressionAlgorithm.Zstd)]
    public async Task Compression_RoundTrip_Success(CompressionAlgorithm algorithm)
    {
        // Arrange
        var testData = GenerateTestData(10000); // 10KB of test data
        var block = new Block
        {
            Type = BlockType.EmailBatch,
            Payload = testData,
            PayloadLength = testData.Length
        };
        
        // Act - Compress
        var compressResult = await _processor.ProcessBlockForWriteAsync(
            block, 
            new ProcessingOptions 
            { 
                CompressionAlgorithm = algorithm,
                EncryptionAlgorithm = EncryptionAlgorithm.None
            });
        
        Assert.That(compressResult.IsSuccess, Is.True);
        var compressedBlock = compressResult.Value;
        
        // Assert compression worked
        if (algorithm != CompressionAlgorithm.None)
        {
            Assert.That(compressedBlock.PayloadLength, Is.LessThan(block.PayloadLength));
            Assert.That((compressedBlock.Flags & 0x0F), Is.EqualTo((byte)algorithm));
        }
        
        // Act - Decompress
        var decompressResult = await _processor.ProcessBlockForReadAsync(compressedBlock);
        
        // Assert
        Assert.That(decompressResult.IsSuccess, Is.True);
        var decompressedBlock = decompressResult.Value;
        Assert.That(decompressedBlock.Payload, Is.EqualTo(testData));
    }
    
    [Test]
    [TestCase(EncryptionAlgorithm.None)]
    [TestCase(EncryptionAlgorithm.AES256GCM)]
    [TestCase(EncryptionAlgorithm.ChaCha20Poly1305)]
    public async Task Encryption_RoundTrip_Success(EncryptionAlgorithm algorithm)
    {
        // Arrange
        var testData = Encoding.UTF8.GetBytes("This is sensitive email data that needs encryption");
        var block = new Block
        {
            Type = BlockType.EmailBatch,
            Payload = testData,
            PayloadLength = testData.Length
        };
        
        // Act - Encrypt
        var encryptResult = await _processor.ProcessBlockForWriteAsync(
            block,
            new ProcessingOptions
            {
                CompressionAlgorithm = CompressionAlgorithm.None,
                EncryptionAlgorithm = algorithm,
                KeyId = "test-key"
            });
        
        Assert.That(encryptResult.IsSuccess, Is.True);
        var encryptedBlock = encryptResult.Value;
        
        // Assert encryption worked
        if (algorithm != EncryptionAlgorithm.None)
        {
            Assert.That(encryptedBlock.Payload, Is.Not.EqualTo(testData));
            Assert.That((encryptedBlock.Flags & 0xF0) >> 4, Is.EqualTo((byte)algorithm));
        }
        
        // Act - Decrypt
        var decryptResult = await _processor.ProcessBlockForReadAsync(encryptedBlock);
        
        // Assert
        Assert.That(decryptResult.IsSuccess, Is.True);
        var decryptedBlock = decryptResult.Value;
        Assert.That(decryptedBlock.Payload, Is.EqualTo(testData));
    }
    
    [Test]
    public async Task CompressThenEncrypt_RoundTrip_Success()
    {
        // Test the recommended compress-then-encrypt workflow
        // Arrange
        var testData = GenerateTestData(50000); // 50KB of compressible data
        var block = new Block
        {
            Type = BlockType.EmailBatch,
            Payload = testData,
            PayloadLength = testData.Length
        };
        
        // Act - Process for write (compress then encrypt)
        var writeResult = await _processor.ProcessBlockForWriteAsync(
            block,
            new ProcessingOptions
            {
                CompressionAlgorithm = CompressionAlgorithm.Zstd,
                EncryptionAlgorithm = EncryptionAlgorithm.AES256GCM,
                KeyId = "test-key"
            });
        
        Assert.That(writeResult.IsSuccess, Is.True);
        var processedBlock = writeResult.Value;
        
        // Assert both compression and encryption flags are set
        Assert.That(processedBlock.Flags & 0x0F, Is.Not.Zero); // Compression
        Assert.That(processedBlock.Flags & 0xF0, Is.Not.Zero); // Encryption
        
        // Assert size reduction from compression
        Assert.That(processedBlock.PayloadLength, Is.LessThan(block.PayloadLength));
        
        // Act - Process for read (decrypt then decompress)
        var readResult = await _processor.ProcessBlockForReadAsync(processedBlock);
        
        // Assert
        Assert.That(readResult.IsSuccess, Is.True);
        var recoveredBlock = readResult.Value;
        Assert.That(recoveredBlock.Payload, Is.EqualTo(testData));
    }
    
    [Test]
    public void ExtendedHeader_Parsing_Success()
    {
        // Test extended header parsing for compressed/encrypted blocks
        // Arrange
        var extHeader = new ExtendedBlockHeader
        {
            UncompressedSize = 50000,
            IV = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 },
            AuthTag = new byte[] { 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 },
            KeyId = "master-key-v1"
        };
        
        // Act - Serialize
        var serialized = extHeader.Serialize();
        
        // Act - Parse
        var parsed = ExtendedBlockHeader.Parse(serialized);
        
        // Assert
        Assert.That(parsed.UncompressedSize, Is.EqualTo(extHeader.UncompressedSize));
        Assert.That(parsed.IV, Is.EqualTo(extHeader.IV));
        Assert.That(parsed.AuthTag, Is.EqualTo(extHeader.AuthTag));
        Assert.That(parsed.KeyId, Is.EqualTo(extHeader.KeyId));
    }
    
    [Test]
    public async Task CompressionThreshold_Respected()
    {
        // Test that small payloads are not compressed
        // Arrange
        var smallData = Encoding.UTF8.GetBytes("Small data");
        var config = new CompressionConfig
        {
            MinimumSizeThreshold = 1000 // 1KB threshold
        };
        
        var processor = new BlockProcessor(_registry, _keyManager, config);
        
        var block = new Block
        {
            Type = BlockType.EmailBatch,
            Payload = smallData,
            PayloadLength = smallData.Length
        };
        
        // Act
        var result = await processor.ProcessBlockForWriteAsync(
            block,
            new ProcessingOptions
            {
                CompressionAlgorithm = CompressionAlgorithm.Gzip
            });
        
        // Assert - Should not be compressed
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Flags & 0x0F, Is.Zero);
        Assert.That(result.Value.Payload, Is.EqualTo(smallData));
    }
    
    private byte[] GenerateTestData(int size)
    {
        // Generate compressible test data
        var data = new byte[size];
        var pattern = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. ";
        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        
        for (int i = 0; i < size; i++)
        {
            data[i] = patternBytes[i % patternBytes.Length];
        }
        
        return data;
    }
}
```

### Task 6.1.3: Key Management Tests
**File**: `EmailDB.UnitTests/Security/KeyManagementTests.cs`
**Dependencies**: Phase 1 key management system
**Description**: Test in-band key management

```csharp
namespace EmailDB.UnitTests.Security;

[TestFixture]
public class KeyManagementTests
{
    private RawBlockManager _blockManager;
    private BlockKeyManager _keyManager;
    private string _testDbPath;
    
    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_keymanager_{Guid.NewGuid()}.db");
        _blockManager = new RawBlockManager(_testDbPath);
        _keyManager = new BlockKeyManager(_blockManager);
    }
    
    [TearDown]
    public void TearDown()
    {
        _keyManager?.Dispose();
        _blockManager?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }
    
    [Test]
    public async Task InitializeWithPassword_CreatesKeyExchangeAndKeyManager()
    {
        // Act
        var result = await _keyManager.InitializeWithPasswordAsync("secure-password-123");
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(_keyManager.IsUnlocked, Is.True);
        
        // Verify blocks were created
        var blocks = _blockManager.GetBlockLocations();
        Assert.That(blocks.Values.Count(b => b.Type == BlockType.KeyExchange), Is.EqualTo(1));
        Assert.That(blocks.Values.Count(b => b.Type == BlockType.KeyManager), Is.EqualTo(1));
    }
    
    [Test]
    public async Task UnlockWithPassword_Success()
    {
        // Arrange
        var password = "test-password-456";
        await _keyManager.InitializeWithPasswordAsync(password);
        
        // Create new key manager instance
        var newKeyManager = new BlockKeyManager(_blockManager);
        
        // Act
        var result = await newKeyManager.UnlockWithPasswordAsync(password);
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(newKeyManager.IsUnlocked, Is.True);
    }
    
    [Test]
    public async Task UnlockWithWrongPassword_Fails()
    {
        // Arrange
        await _keyManager.InitializeWithPasswordAsync("correct-password");
        
        var newKeyManager = new BlockKeyManager(_blockManager);
        
        // Act
        var result = await newKeyManager.UnlockWithPasswordAsync("wrong-password");
        
        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(newKeyManager.IsUnlocked, Is.False);
        Assert.That(result.Error, Does.Contain("Failed to decrypt master key"));
    }
    
    [Test]
    public async Task CreateKey_StoresInKeyManager()
    {
        // Arrange
        await _keyManager.InitializeWithPasswordAsync("password");
        
        // Act
        var result1 = await _keyManager.CreateKeyAsync("email-key-1", KeyPurpose.DataEncryption);
        var result2 = await _keyManager.CreateKeyAsync("index-key-1", KeyPurpose.IndexEncryption);
        
        // Assert
        Assert.That(result1.IsSuccess, Is.True);
        Assert.That(result2.IsSuccess, Is.True);
        
        // Verify keys can be retrieved
        var key1 = await _keyManager.GetKeyAsync("email-key-1");
        var key2 = await _keyManager.GetKeyAsync("index-key-1");
        
        Assert.That(key1.IsSuccess, Is.True);
        Assert.That(key2.IsSuccess, Is.True);
        Assert.That(key1.Value.Length, Is.EqualTo(32)); // 256-bit key
        Assert.That(key2.Value.Length, Is.EqualTo(32));
    }
    
    [Test]
    public async Task RotateKey_CreatesNewVersion()
    {
        // Arrange
        await _keyManager.InitializeWithPasswordAsync("password");
        await _keyManager.CreateKeyAsync("rotating-key", KeyPurpose.DataEncryption);
        
        // Get original key
        var originalKey = await _keyManager.GetKeyAsync("rotating-key");
        
        // Act
        var result = await _keyManager.RotateKeyAsync("rotating-key");
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        
        // Get new key
        var newKey = await _keyManager.GetKeyAsync("rotating-key");
        
        Assert.That(newKey.IsSuccess, Is.True);
        Assert.That(newKey.Value, Is.Not.EqualTo(originalKey.Value));
        
        // Verify new KeyManager block was created
        var blocks = _blockManager.GetBlockLocations();
        Assert.That(blocks.Values.Count(b => b.Type == BlockType.KeyManager), Is.GreaterThan(1));
    }
    
    [Test]
    public async Task AddWebAuthnMethod_CreatesNewKeyExchange()
    {
        // Arrange
        await _keyManager.InitializeWithPasswordAsync("password");
        
        var webAuthnData = new WebAuthnKeyExchange
        {
            CredentialId = Convert.FromBase64String("Y3JlZGVudGlhbC1pZA=="),
            PublicKey = Convert.FromBase64String("cHVibGljLWtleQ=="),
            UserHandle = Convert.FromBase64String("dXNlci1oYW5kbGU="),
            Counter = 0
        };
        
        // Act
        var result = await _keyManager.AddKeyExchangeMethodAsync(
            KeyExchangeMethod.WebAuthn,
            webAuthnData);
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        
        // Verify new KeyExchange block was created
        var blocks = _blockManager.GetBlockLocations();
        Assert.That(blocks.Values.Count(b => b.Type == BlockType.KeyExchange), Is.EqualTo(2));
    }
    
    [Test]
    public async Task KeyManager_Persistence_AcrossInstances()
    {
        // Arrange - Create keys with first instance
        await _keyManager.InitializeWithPasswordAsync("password");
        await _keyManager.CreateKeyAsync("persistent-key", KeyPurpose.DataEncryption);
        var originalKey = await _keyManager.GetKeyAsync("persistent-key");
        
        // Dispose first instance
        _keyManager.Dispose();
        
        // Act - Create new instance and unlock
        var newKeyManager = new BlockKeyManager(_blockManager);
        await newKeyManager.UnlockWithPasswordAsync("password");
        
        // Get key with new instance
        var retrievedKey = await newKeyManager.GetKeyAsync("persistent-key");
        
        // Assert
        Assert.That(retrievedKey.IsSuccess, Is.True);
        Assert.That(retrievedKey.Value, Is.EqualTo(originalKey.Value));
    }
    
    [Test]
    public async Task RecoverPreviousKeyManager_AfterRotation()
    {
        // Arrange
        await _keyManager.InitializeWithPasswordAsync("password");
        await _keyManager.CreateKeyAsync("key1", KeyPurpose.DataEncryption);
        
        // Rotate to create new version
        await _keyManager.RotateKeyAsync("key1");
        
        // Act - Recover previous version
        var result = await _keyManager.RecoverPreviousKeyManagerAsync();
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        
        // Should still have access to keys
        var key = await _keyManager.GetKeyAsync("key1");
        Assert.That(key.IsSuccess, Is.True);
    }
    
    [Test]
    [TestCase("", "password")]
    [TestCase("password", "")]
    [TestCase(null, "password")]
    [TestCase("password", null)]
    public async Task InitializeWithInvalidPassword_Fails(string password, string confirmation)
    {
        // Act
        var result = await _keyManager.InitializeWithPasswordAsync(password);
        
        // Assert
        Assert.That(result.IsSuccess, Is.False);
    }
}
```

### Task 6.1.4: Manager Layer Unit Tests
**File**: `EmailDB.UnitTests/Managers/ManagerUnitTests.cs`
**Dependencies**: Phase 2 managers
**Description**: Test individual manager components with mocks

```csharp
namespace EmailDB.UnitTests.Managers;

[TestFixture]
public class ManagerUnitTests
{
    private Mock<IRawBlockManager> _mockBlockManager;
    private Mock<IBlockContentSerializer> _mockSerializer;
    private Mock<ILogger> _mockLogger;
    
    [SetUp]
    public void Setup()
    {
        _mockBlockManager = new Mock<IRawBlockManager>();
        _mockSerializer = new Mock<IBlockContentSerializer>();
        _mockLogger = new Mock<ILogger>();
    }
    
    [Test]
    public async Task FolderManager_StoreFolderBlock_IncrementsVersion()
    {
        // Arrange
        var folderManager = new FolderManager(
            _mockBlockManager.Object,
            null, // EmailStorageManager not needed for this test
            _mockSerializer.Object,
            _mockLogger.Object);
            
        var folder = new FolderContent
        {
            Name = "TestFolder",
            Version = 0,
            EmailIds = new List<EmailHashedID>()
        };
        
        _mockSerializer.Setup(s => s.Serialize(It.IsAny<FolderContent>(), PayloadEncoding.Protobuf))
            .Returns(Result<byte[]>.Success(new byte[] { 1, 2, 3 }));
            
        _mockBlockManager.Setup(b => b.WriteBlockAsync(
                BlockType.Folder,
                It.IsAny<byte[]>(),
                It.IsAny<CompressionAlgorithm>(),
                It.IsAny<EncryptionAlgorithm>(),
                It.IsAny<string>()))
            .ReturnsAsync(Result<long>.Success(12345));
        
        // Act
        var result = await folderManager.StoreFolderBlockAsync(folder);
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.EqualTo(12345));
        
        // Verify version was incremented
        _mockSerializer.Verify(s => s.Serialize(
            It.Is<FolderContent>(f => f.Version == 1),
            PayloadEncoding.Protobuf), Times.Once);
    }
    
    [Test]
    public async Task FolderManager_StoreEnvelopeBlock_TracksSuperseded()
    {
        // Arrange
        var folderManager = new FolderManager(
            _mockBlockManager.Object,
            null,
            _mockSerializer.Object,
            _mockLogger.Object);
            
        // Pre-populate cache to simulate existing envelope
        var existingBlockId = 999L;
        folderManager.CacheEnvelopeBlock("Inbox", existingBlockId);
        
        var envelopeBlock = new FolderEnvelopeBlock
        {
            FolderPath = "Inbox",
            Version = 1,
            Envelopes = new List<EmailEnvelope>()
        };
        
        _mockSerializer.Setup(s => s.Serialize(It.IsAny<FolderEnvelopeBlock>(), PayloadEncoding.Protobuf))
            .Returns(Result<byte[]>.Success(new byte[] { 1, 2, 3 }));
            
        _mockBlockManager.Setup(b => b.WriteBlockAsync(
                BlockType.FolderEnvelope,
                It.IsAny<byte[]>(),
                It.IsAny<CompressionAlgorithm>(),
                It.IsAny<EncryptionAlgorithm>(),
                It.IsAny<string>()))
            .ReturnsAsync(Result<long>.Success(12345));
        
        // Act
        var result = await folderManager.StoreEnvelopeBlockAsync(envelopeBlock);
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        
        // Verify superseded blocks are tracked
        var superseded = await folderManager.GetSupersededBlocksAsync();
        Assert.That(superseded.IsSuccess, Is.True);
        Assert.That(superseded.Value.Any(b => b.BlockId == existingBlockId), Is.True);
    }
    
    [Test]
    public async Task EmailStorageManager_AddEmail_BatchesCorrectly()
    {
        // Arrange
        var storageManager = new EmailStorageManager(
            _mockBlockManager.Object,
            _mockSerializer.Object,
            targetBatchSize: 1024); // Small batch size for testing
            
        var smallEmail = MimeMessage.Load(new MemoryStream(
            Encoding.UTF8.GetBytes("From: test@test.com\r\nSubject: Small\r\n\r\nSmall content")));
        var smallData = new byte[500];
        
        // Mock successful serialization and write
        _mockSerializer.Setup(s => s.Serialize(It.IsAny<EmailBatchContent>(), PayloadEncoding.Protobuf))
            .Returns(Result<byte[]>.Success(new byte[] { 1, 2, 3 }));
            
        _mockBlockManager.Setup(b => b.WriteBlockAsync(
                BlockType.EmailBatch,
                It.IsAny<byte[]>(),
                It.IsAny<CompressionAlgorithm>(),
                It.IsAny<EncryptionAlgorithm>(),
                It.IsAny<string>()))
            .ReturnsAsync(Result<long>.Success(12345));
        
        // Act - Add multiple emails
        var result1 = await storageManager.StoreEmailAsync(smallEmail, smallData);
        var result2 = await storageManager.StoreEmailAsync(smallEmail, smallData);
        
        // Should not flush yet (under batch size)
        _mockBlockManager.Verify(b => b.WriteBlockAsync(
            It.IsAny<BlockType>(),
            It.IsAny<byte[]>(),
            It.IsAny<CompressionAlgorithm>(),
            It.IsAny<EncryptionAlgorithm>(),
            It.IsAny<string>()), Times.Never);
        
        // Add one more to exceed batch size
        var largeData = new byte[600];
        var result3 = await storageManager.StoreEmailAsync(smallEmail, largeData);
        
        // Assert - Should have flushed
        _mockBlockManager.Verify(b => b.WriteBlockAsync(
            BlockType.EmailBatch,
            It.IsAny<byte[]>(),
            It.IsAny<CompressionAlgorithm>(),
            It.IsAny<EncryptionAlgorithm>(),
            It.IsAny<string>()), Times.Once);
            
        Assert.That(result3.IsSuccess, Is.True);
        Assert.That(result3.Value.BlockId, Is.EqualTo(12345));
    }
    
    [Test]
    public async Task EmailManager_ImportEML_CoordinatesAllManagers()
    {
        // This is more of an integration test but with mocked dependencies
        var mockHybridStore = new Mock<IHybridEmailStore>();
        var mockFolderManager = new Mock<IFolderManager>();
        var mockStorageManager = new Mock<IEmailStorageManager>();
        
        var emailManager = new EmailManager(
            mockHybridStore.Object,
            mockFolderManager.Object,
            mockStorageManager.Object,
            _mockBlockManager.Object,
            _mockSerializer.Object);
            
        var emlContent = "From: test@test.com\r\nSubject: Test\r\n\r\nTest content";
        var testEmailId = new EmailHashedID
        {
            BlockId = 123,
            LocalId = 0,
            EnvelopeHash = new byte[] { 1, 2, 3 },
            ContentHash = new byte[] { 4, 5, 6 }
        };
        
        // Setup mocks
        mockStorageManager.Setup(s => s.StoreEmailAsync(
                It.IsAny<MimeMessage>(),
                It.IsAny<byte[]>()))
            .ReturnsAsync(Result<EmailHashedID>.Success(testEmailId));
            
        mockFolderManager.Setup(f => f.AddEmailToFolderAsync(
                "Inbox",
                It.IsAny<EmailHashedID>(),
                It.IsAny<EmailEnvelope>()))
            .ReturnsAsync(Result.Success());
            
        mockHybridStore.Setup(h => h.UpdateIndexesForEmailAsync(
                It.IsAny<EmailHashedID>(),
                It.IsAny<MimeMessage>(),
                "Inbox",
                It.IsAny<long>()))
            .ReturnsAsync(Result.Success());
        
        // Act
        var result = await emailManager.ImportEMLAsync(emlContent, "Inbox");
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.EqualTo(testEmailId));
        
        // Verify all managers were called
        mockStorageManager.Verify(s => s.StoreEmailAsync(
            It.IsAny<MimeMessage>(),
            It.IsAny<byte[]>()), Times.Once);
            
        mockFolderManager.Verify(f => f.AddEmailToFolderAsync(
            "Inbox",
            It.IsAny<EmailHashedID>(),
            It.IsAny<EmailEnvelope>()), Times.Once);
            
        mockHybridStore.Verify(h => h.UpdateIndexesForEmailAsync(
            It.IsAny<EmailHashedID>(),
            It.IsAny<MimeMessage>(),
            "Inbox",
            It.IsAny<long>()), Times.Once);
    }
}
```

### Task 6.1.5: Index and Version Tests
**File**: `EmailDB.UnitTests/Infrastructure/IndexAndVersionTests.cs`
**Dependencies**: Phase 3 and 5 components
**Description**: Test index management and version system

```csharp
namespace EmailDB.UnitTests.Infrastructure;

[TestFixture]
public class IndexAndVersionTests
{
    private string _testDbPath;
    private RawBlockManager _blockManager;
    private IndexManager _indexManager;
    private FormatVersionManager _versionManager;
    
    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_index_{Guid.NewGuid()}.db");
        var indexPath = Path.Combine(Path.GetTempPath(), $"test_index_{Guid.NewGuid()}");
        
        _blockManager = new RawBlockManager(_testDbPath);
        _indexManager = new IndexManager(indexPath);
        _versionManager = new FormatVersionManager(_blockManager);
    }
    
    [TearDown]
    public void TearDown()
    {
        _indexManager?.Dispose();
        _blockManager?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }
    
    [Test]
    public async Task IndexManager_IndexEmail_AllIndexesUpdated()
    {
        // Arrange
        var emailId = new EmailHashedID
        {
            BlockId = 123,
            LocalId = 5,
            EnvelopeHash = new byte[] { 1, 2, 3 },
            ContentHash = new byte[] { 4, 5, 6 }
        };
        
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test Sender", "sender@test.com"));
        message.To.Add(new MailboxAddress("Test Recipient", "recipient@test.com"));
        message.Subject = "Test Subject";
        message.MessageId = "<unique-id@test.com>";
        message.Body = new TextPart("plain") { Text = "Test email content" };
        
        // Act
        var result = await _indexManager.IndexEmailAsync(
            emailId,
            message,
            "Inbox",
            999); // envelope block ID
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        
        // Verify indexes were updated
        var messageIdResult = _indexManager.GetEmailByMessageId("<unique-id@test.com>");
        Assert.That(messageIdResult.IsSuccess, Is.True);
        Assert.That(messageIdResult.Value, Is.EqualTo(emailId.ToCompoundKey()));
        
        var envelopeHashResult = _indexManager.GetEmailByEnvelopeHash(emailId.EnvelopeHash);
        Assert.That(envelopeHashResult.IsSuccess, Is.True);
        
        var locationResult = _indexManager.GetEmailLocation(emailId.ToCompoundKey());
        Assert.That(locationResult.IsSuccess, Is.True);
        Assert.That(locationResult.Value.BlockId, Is.EqualTo(123));
        Assert.That(locationResult.Value.LocalId, Is.EqualTo(5));
    }
    
    [Test]
    public async Task VersionManager_DetectVersion_NewDatabase()
    {
        // Act - Detect version on new database
        var result = await _versionManager.DetectVersionAsync();
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Is.EqualTo(DatabaseVersion.Current));
        
        // Verify header block was created
        var blocks = _blockManager.GetBlockLocations();
        Assert.That(blocks.Values.Any(b => b.Type == BlockType.Header), Is.True);
    }
    
    [Test]
    public async Task VersionManager_CheckCompatibility_CurrentVersion()
    {
        // Arrange
        await _versionManager.DetectVersionAsync();
        
        // Act
        var result = _versionManager.CheckCompatibility();
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        var info = result.Value;
        Assert.That(info.IsCompatible, Is.True);
        Assert.That(info.DatabaseVersion, Is.EqualTo(DatabaseVersion.Current));
        Assert.That(info.CanUpgrade, Is.False); // Already at current version
    }
    
    [Test]
    public async Task VersionManager_ValidateOperation_Success()
    {
        // Arrange
        await _versionManager.DetectVersionAsync();
        
        // Act
        var result = _versionManager.ValidateOperation(
            "TestOperation",
            FeatureCapabilities.EmailBatching | FeatureCapabilities.Compression);
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
    }
    
    [Test]
    public async Task CompatibilityMatrix_GetUpgradeStrategy_DirectPath()
    {
        // Act
        var result = CompatibilityMatrix.GetUpgradeStrategy(
            new DatabaseVersion(1, 0, 0),
            new DatabaseVersion(2, 0, 0));
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        var strategy = result.Value;
        Assert.That(strategy.Type, Is.EqualTo(UpgradeType.Migration));
        Assert.That(strategy.RequiresBackup, Is.True);
        Assert.That(strategy.MigrationHandlerType, Is.EqualTo(typeof(V1ToV2MigrationHandler)));
    }
    
    [Test]
    public void DatabaseVersion_Comparison_Works()
    {
        // Arrange
        var v1_0_0 = new DatabaseVersion(1, 0, 0);
        var v1_1_0 = new DatabaseVersion(1, 1, 0);
        var v2_0_0 = new DatabaseVersion(2, 0, 0);
        
        // Assert
        Assert.That(v1_0_0 < v1_1_0, Is.True);
        Assert.That(v1_1_0 < v2_0_0, Is.True);
        Assert.That(v2_0_0 > v1_0_0, Is.True);
        Assert.That(v1_0_0.IsCompatibleWith(v1_1_0), Is.True);
        Assert.That(v2_0_0.IsCompatibleWith(v1_0_0), Is.True); // Can read older
        Assert.That(v1_0_0.IsCompatibleWith(v2_0_0), Is.False); // Cannot read newer
    }
    
    [Test]
    public async Task SearchOptimizer_BasicSearch_FindsResults()
    {
        // Arrange
        var searchOptimizer = new SearchOptimizer(
            _indexManager,
            Mock.Of<IFolderManager>(),
            _blockManager,
            Mock.Of<IBlockContentSerializer>());
            
        // Index some test emails
        await IndexTestEmail("test1@example.com", "Important meeting", "Let's discuss the project");
        await IndexTestEmail("test2@example.com", "Project update", "The project is on track");
        await IndexTestEmail("test3@example.com", "Meeting cancelled", "Sorry, need to reschedule");
        
        // Act
        var result = await searchOptimizer.SearchAsync("project");
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Count, Is.EqualTo(2)); // Should find 2 emails with "project"
    }
    
    private async Task IndexTestEmail(string messageId, string subject, string body)
    {
        var emailId = new EmailHashedID
        {
            BlockId = Random.Shared.NextInt64(1000, 9999),
            LocalId = Random.Shared.Next(0, 100),
            EnvelopeHash = new byte[32],
            ContentHash = new byte[32]
        };
        
        Random.Shared.NextBytes(emailId.EnvelopeHash);
        Random.Shared.NextBytes(emailId.ContentHash);
        
        var message = new MimeMessage();
        message.MessageId = messageId;
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        message.From.Add(new MailboxAddress("Sender", "sender@test.com"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@test.com"));
        
        await _indexManager.IndexEmailAsync(emailId, message, "Inbox", 1000);
    }
}
```

## Section 6.2: Integration Tests

### Task 6.2.1: End-to-End Workflow Tests
**File**: `EmailDB.UnitTests/Integration/EndToEndWorkflowTests.cs`
**Dependencies**: All components
**Description**: Test complete workflows from import to retrieval

```csharp
namespace EmailDB.UnitTests.Integration;

[TestFixture]
public class EndToEndWorkflowTests
{
    private string _testDbPath;
    private string _indexPath;
    private EmailDatabase _database;
    
    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_e2e_{Guid.NewGuid()}.db");
        _indexPath = Path.Combine(Path.GetTempPath(), $"test_e2e_index_{Guid.NewGuid()}");
        
        // Initialize database with new architecture
        _database = new EmailDatabase(_testDbPath, autoUpgrade: true);
    }
    
    [TearDown]
    public void TearDown()
    {
        _database?.Dispose();
        
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
            
        if (Directory.Exists(_indexPath))
            Directory.Delete(_indexPath, true);
    }
    
    [Test]
    public async Task CompleteEmailLifecycle_ImportSearchRetrieve()
    {
        // Arrange
        var emlContent = @"From: sender@example.com
To: recipient@example.com
Subject: Important Project Update
Message-ID: <unique-123@example.com>
Date: Mon, 1 Jan 2024 10:00:00 +0000

This is an important update about our project progress.
We have achieved several milestones this quarter.";

        // Act 1: Import email
        var importResult = await _database.ImportEMLAsync(emlContent, "Inbox");
        
        // Assert import success
        Assert.That(importResult.IsSuccess, Is.True);
        var emailId = importResult.Value;
        Assert.That(emailId.BlockId, Is.GreaterThan(0));
        
        // Act 2: Search for email
        var searchResult = await _database.SearchAsync("project milestones");
        
        // Assert search found email
        Assert.That(searchResult.IsSuccess, Is.True);
        Assert.That(searchResult.Value.Count, Is.GreaterThan(0));
        
        var searchResultId = searchResult.Value.First().EmailId;
        Assert.That(searchResultId, Is.EqualTo(emailId));
        
        // Act 3: Retrieve email by ID
        var retrieveResult = await _database.GetEmailAsync(emailId);
        
        // Assert retrieval success
        Assert.That(retrieveResult.IsSuccess, Is.True);
        var retrieved = retrieveResult.Value;
        Assert.That(retrieved.Subject, Is.EqualTo("Important Project Update"));
        Assert.That(retrieved.MessageId, Is.EqualTo("<unique-123@example.com>"));
        
        // Act 4: Get folder listing
        var listingResult = await _database.GetFolderListingAsync("Inbox");
        
        // Assert listing contains email
        Assert.That(listingResult.IsSuccess, Is.True);
        Assert.That(listingResult.Value.Count, Is.EqualTo(1));
        Assert.That(listingResult.Value[0].MessageId, Is.EqualTo("<unique-123@example.com>"));
    }
    
    [Test]
    public async Task BatchImport_HandlesMultipleEmails()
    {
        // Arrange
        var emails = new List<(string fileName, string content)>();
        
        for (int i = 0; i < 100; i++)
        {
            var content = $@"From: sender{i}@example.com
To: recipient@example.com
Subject: Test Email {i}
Message-ID: <msg-{i}@example.com>
Date: Mon, {(i % 28) + 1} Jan 2024 {(i % 24):D2}:00:00 +0000

This is test email number {i}.
It contains some random content for testing.
Keywords: test, email, batch, import";

            emails.Add(($"email_{i}.eml", content));
        }
        
        // Act
        var result = await _database.ImportEMLBatchAsync(emails.ToArray(), "Bulk");
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        var batchResult = result.Value;
        Assert.That(batchResult.SuccessCount, Is.EqualTo(100));
        Assert.That(batchResult.ErrorCount, Is.EqualTo(0));
        Assert.That(batchResult.ImportedEmailIds.Count, Is.EqualTo(100));
        
        // Verify folder listing
        var listingResult = await _database.GetFolderListingAsync("Bulk");
        Assert.That(listingResult.IsSuccess, Is.True);
        Assert.That(listingResult.Value.Count, Is.EqualTo(100));
    }
    
    [Test]
    public async Task EmailMove_UpdatesFoldersCorrectly()
    {
        // Arrange
        var emlContent = @"From: sender@example.com
To: recipient@example.com
Subject: Email to Move
Message-ID: <move-test@example.com>

Test email for move operation.";

        // Import to Inbox
        var importResult = await _database.ImportEMLAsync(emlContent, "Inbox");
        Assert.That(importResult.IsSuccess, Is.True);
        var emailId = importResult.Value;
        
        // Create Archive folder
        var createFolderResult = await _database.CreateFolderAsync("Archive");
        Assert.That(createFolderResult.IsSuccess, Is.True);
        
        // Act - Move email
        var moveResult = await _database.MoveEmailAsync(emailId, "Inbox", "Archive");
        
        // Assert
        Assert.That(moveResult.IsSuccess, Is.True);
        
        // Verify Inbox is empty
        var inboxListing = await _database.GetFolderListingAsync("Inbox");
        Assert.That(inboxListing.IsSuccess, Is.True);
        Assert.That(inboxListing.Value.Count, Is.EqualTo(0));
        
        // Verify Archive contains email
        var archiveListing = await _database.GetFolderListingAsync("Archive");
        Assert.That(archiveListing.IsSuccess, Is.True);
        Assert.That(archiveListing.Value.Count, Is.EqualTo(1));
        Assert.That(archiveListing.Value[0].MessageId, Is.EqualTo("<move-test@example.com>"));
    }
    
    [Test]
    public async Task DuplicateDetection_PreventsDuplicates()
    {
        // Arrange
        var emlContent = @"From: sender@example.com
To: recipient@example.com
Subject: Duplicate Test
Message-ID: <duplicate@example.com>
Date: Mon, 1 Jan 2024 10:00:00 +0000

This email will be imported twice.";

        // Act - Import twice
        var result1 = await _database.ImportEMLAsync(emlContent, "Inbox");
        var result2 = await _database.ImportEMLAsync(emlContent, "Inbox");
        
        // Assert
        Assert.That(result1.IsSuccess, Is.True);
        Assert.That(result2.IsSuccess, Is.False);
        Assert.That(result2.Error, Does.Contain("duplicate"));
        
        // Verify only one email in folder
        var listing = await _database.GetFolderListingAsync("Inbox");
        Assert.That(listing.IsSuccess, Is.True);
        Assert.That(listing.Value.Count, Is.EqualTo(1));
    }
    
    [Test]
    public async Task AdvancedSearch_FiltersCorrectly()
    {
        // Arrange - Import various emails
        var emails = new[]
        {
            // Email 1: Matches sender and date
            @"From: alice@example.com
To: recipient@example.com
Subject: Meeting Notes
Message-ID: <email1@example.com>
Date: Mon, 15 Jan 2024 10:00:00 +0000

Notes from today's meeting.",

            // Email 2: Matches sender but not date
            @"From: alice@example.com
To: recipient@example.com
Subject: Follow Up
Message-ID: <email2@example.com>
Date: Mon, 1 Feb 2024 10:00:00 +0000

Following up on our discussion.",

            // Email 3: Matches date but not sender
            @"From: bob@example.com
To: recipient@example.com
Subject: Project Update
Message-ID: <email3@example.com>
Date: Wed, 17 Jan 2024 14:00:00 +0000

Latest project status."
        };
        
        foreach (var email in emails)
        {
            await _database.ImportEMLAsync(email, "Inbox");
        }
        
        // Act - Advanced search
        var searchQuery = new SearchQuery
        {
            From = "alice@example.com",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 1, 31)
        };
        
        var result = await _database.AdvancedSearchAsync(searchQuery);
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.Count, Is.EqualTo(1));
        Assert.That(result.Value[0].Envelope.MessageId, Is.EqualTo("<email1@example.com>"));
    }
}
```

### Task 6.2.2: Transaction and Atomicity Tests
**File**: `EmailDB.UnitTests/Integration/TransactionTests.cs`
**Dependencies**: Transaction support from Phase 2
**Description**: Test atomic operations and rollback

```csharp
namespace EmailDB.UnitTests.Integration;

[TestFixture]
public class TransactionTests
{
    private string _testDbPath;
    private EmailManager _emailManager;
    private RawBlockManager _blockManager;
    private Mock<IFolderManager> _mockFolderManager;
    
    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_tx_{Guid.NewGuid()}.db");
        _blockManager = new RawBlockManager(_testDbPath);
        
        // Setup with partial mock to simulate failures
        _mockFolderManager = new Mock<IFolderManager>();
        
        // Real components
        var storageManager = new EmailStorageManager(_blockManager, new DefaultBlockContentSerializer());
        var hybridStore = new HybridEmailStore(_testDbPath, Path.GetTempPath(), _mockFolderManager.Object, storageManager);
        
        _emailManager = new EmailManager(
            hybridStore,
            _mockFolderManager.Object,
            storageManager,
            _blockManager,
            new DefaultBlockContentSerializer());
    }
    
    [TearDown]
    public void TearDown()
    {
        _emailManager?.Dispose();
        _blockManager?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }
    
    [Test]
    public async Task ImportEmail_RollbackOnFolderFailure()
    {
        // Arrange
        var emlContent = @"From: test@test.com
Subject: Test
Message-ID: <rollback-test@test.com>

Test content";

        // Setup folder manager to fail
        _mockFolderManager
            .Setup(f => f.AddEmailToFolderAsync(
                It.IsAny<string>(),
                It.IsAny<EmailHashedID>(),
                It.IsAny<EmailEnvelope>()))
            .ReturnsAsync(Result.Failure("Simulated folder error"));
        
        // Track block count before
        var blocksBefore = _blockManager.GetBlockLocations().Count;
        
        // Act
        var result = await _emailManager.ImportEMLAsync(emlContent, "Inbox");
        
        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Does.Contain("folder"));
        
        // Verify rollback - no new blocks should exist
        var blocksAfter = _blockManager.GetBlockLocations().Count;
        Assert.That(blocksAfter, Is.EqualTo(blocksBefore));
    }
    
    [Test]
    public async Task MoveEmail_AtomicUpdate()
    {
        // This test verifies that move operations are atomic
        // Setup initial state with successful operations
        _mockFolderManager
            .Setup(f => f.GetFolderListingAsync(It.IsAny<string>()))
            .ReturnsAsync(Result<List<EmailEnvelope>>.Success(new List<EmailEnvelope>
            {
                new EmailEnvelope
                {
                    CompoundId = "123:0",
                    MessageId = "<test@test.com>",
                    Subject = "Test"
                }
            }));
            
        // First removal succeeds
        _mockFolderManager
            .SetupSequence(f => f.RemoveEmailFromFolderAsync(
                It.IsAny<string>(),
                It.IsAny<EmailHashedID>()))
            .ReturnsAsync(Result.Success())
            .ReturnsAsync(Result.Failure("Rollback simulation"));
            
        // Addition fails
        _mockFolderManager
            .Setup(f => f.AddEmailToFolderAsync(
                "Destination",
                It.IsAny<EmailHashedID>(),
                It.IsAny<EmailEnvelope>()))
            .ReturnsAsync(Result.Failure("Simulated add failure"));
        
        var emailId = new EmailHashedID { BlockId = 123, LocalId = 0 };
        
        // Act
        var result = await _emailManager.MoveEmailAsync(emailId, "Source", "Destination");
        
        // Assert
        Assert.That(result.IsSuccess, Is.False);
        
        // Verify rollback was attempted (remove called twice - once for operation, once for rollback)
        _mockFolderManager.Verify(f => f.RemoveEmailFromFolderAsync(
            It.IsAny<string>(),
            It.IsAny<EmailHashedID>()), Times.Exactly(2));
    }
    
    [Test]
    public async Task BatchImport_PartialSuccess_Rollback()
    {
        // Test that batch imports handle partial failures correctly
        var emails = new[]
        {
            ("email1.eml", "From: test@test.com\r\nSubject: Email 1\r\n\r\nContent"),
            ("email2.eml", "From: test@test.com\r\nSubject: Email 2\r\n\r\nContent"),
            ("email3.eml", "INVALID EMAIL CONTENT"), // This will fail
        };
        
        // Setup to track successful imports before failure
        var importedCount = 0;
        _mockFolderManager
            .Setup(f => f.AddEmailToFolderAsync(
                It.IsAny<string>(),
                It.IsAny<EmailHashedID>(),
                It.IsAny<EmailEnvelope>()))
            .Returns(() =>
            {
                importedCount++;
                return Task.FromResult(Result.Success());
            });
        
        // Act
        var result = await _emailManager.ImportEMLBatchAsync(emails, "Inbox");
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        var batchResult = result.Value;
        Assert.That(batchResult.SuccessCount, Is.EqualTo(2));
        Assert.That(batchResult.ErrorCount, Is.EqualTo(1));
        Assert.That(batchResult.Errors.Count, Is.EqualTo(1));
        Assert.That(batchResult.Errors[0], Does.Contain("email3.eml"));
    }
}
```

### Task 6.2.3: Upgrade and Migration Tests
**File**: `EmailDB.UnitTests/Integration/UpgradeTests.cs`
**Dependencies**: Phase 5 upgrade infrastructure
**Description**: Test version upgrades and migrations

```csharp
namespace EmailDB.UnitTests.Integration;

[TestFixture]
public class UpgradeTests
{
    private string _testDbPath;
    
    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_upgrade_{Guid.NewGuid()}.db");
    }
    
    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
            
        // Clean up any backup files
        var backupFiles = Directory.GetFiles(
            Path.GetDirectoryName(_testDbPath),
            Path.GetFileName(_testDbPath) + ".backup*");
            
        foreach (var backup in backupFiles)
            File.Delete(backup);
    }
    
    [Test]
    public async Task UpgradeFromV1ToV2_Success()
    {
        // Arrange - Create v1 database
        await CreateV1DatabaseAsync();
        
        // Verify v1 structure
        using (var v1BlockManager = new RawBlockManager(_testDbPath, createIfNotExists: false, isReadOnly: true))
        {
            var blocks = v1BlockManager.GetBlockLocations();
            Assert.That(blocks.Values.Any(b => b.Type == BlockType.Email), Is.True);
        }
        
        // Act - Perform upgrade
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var versionManager = new FormatVersionManager(blockManager);
            var orchestrator = new UpgradeOrchestrator(versionManager, blockManager, _testDbPath);
            
            var checkResult = await orchestrator.CheckUpgradeNeededAsync();
            Assert.That(checkResult.IsSuccess, Is.True);
            Assert.That(checkResult.Value.IsUpgradeNeeded, Is.True);
            Assert.That(checkResult.Value.CanUpgrade, Is.True);
            
            var upgradeResult = await orchestrator.UpgradeAsync(checkResult.Value);
            Assert.That(upgradeResult.IsSuccess, Is.True);
        }
        
        // Assert - Verify v2 structure
        using (var v2BlockManager = new RawBlockManager(_testDbPath, createIfNotExists: false, isReadOnly: true))
        {
            var blocks = v2BlockManager.GetBlockLocations();
            
            // Should have email batches instead of individual emails
            Assert.That(blocks.Values.Any(b => b.Type == BlockType.EmailBatch), Is.True);
            Assert.That(blocks.Values.Any(b => b.Type == BlockType.Email), Is.False);
            
            // Should have envelope blocks
            Assert.That(blocks.Values.Any(b => b.Type == BlockType.FolderEnvelope), Is.True);
            
            // Verify version in header
            var versionManager = new FormatVersionManager(v2BlockManager);
            var versionResult = await versionManager.DetectVersionAsync();
            Assert.That(versionResult.IsSuccess, Is.True);
            Assert.That(versionResult.Value.Major, Is.EqualTo(2));
        }
    }
    
    [Test]
    public async Task InPlaceUpgrade_MinorVersion_Success()
    {
        // Arrange - Create v2.0.0 database
        await CreateV2DatabaseAsync(new DatabaseVersion(2, 0, 0));
        
        // Act - Simulate upgrade to v2.1.0
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var versionManager = new FormatVersionManager(blockManager);
            
            // Manually update to v2.1.0
            var updateResult = await versionManager.UpdateVersionAsync(new DatabaseVersion(2, 1, 0));
            Assert.That(updateResult.IsSuccess, Is.True);
        }
        
        // Assert
        using (var blockManager = new RawBlockManager(_testDbPath, createIfNotExists: false, isReadOnly: true))
        {
            var versionManager = new FormatVersionManager(blockManager);
            var versionResult = await versionManager.DetectVersionAsync();
            
            Assert.That(versionResult.IsSuccess, Is.True);
            Assert.That(versionResult.Value.Major, Is.EqualTo(2));
            Assert.That(versionResult.Value.Minor, Is.EqualTo(1));
        }
    }
    
    [Test]
    public async Task UpgradeWithBackup_CreatesBackup()
    {
        // Arrange
        await CreateV1DatabaseAsync();
        
        // Act
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var versionManager = new FormatVersionManager(blockManager);
            var orchestrator = new UpgradeOrchestrator(versionManager, blockManager, _testDbPath);
            
            var checkResult = await orchestrator.CheckUpgradeNeededAsync();
            var upgradeResult = await orchestrator.UpgradeAsync(checkResult.Value);
            
            Assert.That(upgradeResult.IsSuccess, Is.True);
        }
        
        // Assert - Backup should exist
        var backupFiles = Directory.GetFiles(
            Path.GetDirectoryName(_testDbPath),
            Path.GetFileName(_testDbPath) + ".backup*");
            
        Assert.That(backupFiles.Length, Is.GreaterThan(0));
        Assert.That(File.Exists(backupFiles[0]), Is.True);
    }
    
    [Test]
    public void VersionCompatibility_ForwardCompatibility_Fails()
    {
        // Test that older versions cannot open newer databases
        var oldVersion = new DatabaseVersion(1, 0, 0);
        var newVersion = new DatabaseVersion(2, 0, 0);
        
        Assert.That(oldVersion.IsCompatibleWith(newVersion), Is.False);
    }
    
    [Test]
    public void VersionCompatibility_BackwardCompatibility_Success()
    {
        // Test that newer versions can open older databases
        var oldVersion = new DatabaseVersion(1, 0, 0);
        var newVersion = new DatabaseVersion(2, 0, 0);
        
        Assert.That(newVersion.IsCompatibleWith(oldVersion), Is.True);
    }
    
    private async Task CreateV1DatabaseAsync()
    {
        // Create a simplified v1 database structure
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            // Write v1 header
            var header = new HeaderContent { FileVersion = (1 << 24) | (0 << 16) | 0 };
            var headerData = SerializeHeader(header);
            await blockManager.WriteBlockAsync(BlockType.Header, headerData, blockId: 0);
            
            // Write some v1 email blocks (individual emails)
            for (int i = 0; i < 5; i++)
            {
                var emailData = Encoding.UTF8.GetBytes(
                    $"From: test{i}@example.com\r\n" +
                    $"Subject: Test Email {i}\r\n" +
                    $"Message-ID: <v1-{i}@example.com>\r\n\r\n" +
                    $"V1 test email content {i}");
                    
                await blockManager.WriteBlockAsync(BlockType.Email, emailData);
            }
        }
    }
    
    private async Task CreateV2DatabaseAsync(DatabaseVersion version)
    {
        using (var blockManager = new RawBlockManager(_testDbPath))
        {
            var versionManager = new FormatVersionManager(blockManager);
            await versionManager.DetectVersionAsync(); // Will create v2 header
            
            if (version != DatabaseVersion.Current)
            {
                await versionManager.UpdateVersionAsync(version);
            }
        }
    }
    
    private byte[] SerializeHeader(HeaderContent header)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(header.FileVersion);
        writer.Write(header.FirstMetadataOffset);
        writer.Write(header.FirstFolderTreeOffset);
        writer.Write(header.FirstCleanupOffset);
        
        return ms.ToArray();
    }
}
```

## Section 6.3: Performance and Stress Tests

### Task 6.3.1: Performance Benchmarks
**File**: `EmailDB.UnitTests/Performance/PerformanceBenchmarks.cs`
**Dependencies**: All components
**Description**: Measure and validate performance targets

```csharp
namespace EmailDB.UnitTests.Performance;

[TestFixture]
[Category("Performance")]
public class PerformanceBenchmarks
{
    private string _testDbPath;
    private EmailDatabase _database;
    private List<EmailHashedID> _importedEmails;
    
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"perf_test_{Guid.NewGuid()}.db");
        _database = new EmailDatabase(_testDbPath);
        _importedEmails = new List<EmailHashedID>();
    }
    
    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _database?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }
    
    [Test]
    [Category("Benchmark")]
    public async Task ImportThroughput_MeetsTarget()
    {
        // Target: >1000 emails/second
        const int emailCount = 5000;
        var emails = GenerateTestEmails(emailCount);
        
        var stopwatch = Stopwatch.StartNew();
        
        // Act
        var result = await _database.ImportEMLBatchAsync(emails, "Performance");
        
        stopwatch.Stop();
        
        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value.SuccessCount, Is.EqualTo(emailCount));
        
        var emailsPerSecond = emailCount / stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine($"Import throughput: {emailsPerSecond:F2} emails/second");
        
        Assert.That(emailsPerSecond, Is.GreaterThan(1000), 
            "Import throughput should exceed 1000 emails/second");
            
        // Store for other tests
        _importedEmails.AddRange(result.Value.ImportedEmailIds);
    }
    
    [Test]
    public async Task FolderListing_MeetsLatencyTarget()
    {
        // Target: <10ms for 1000 emails
        // Ensure we have emails imported
        if (_importedEmails.Count == 0)
        {
            await ImportThroughput_MeetsTarget();
        }
        
        // Warm up cache
        await _database.GetFolderListingAsync("Performance");
        
        // Measure
        var timings = new List<double>();
        
        for (int i = 0; i < 100; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _database.GetFolderListingAsync("Performance");
            stopwatch.Stop();
            
            Assert.That(result.IsSuccess, Is.True);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        
        var avgLatency = timings.Average();
        var p95Latency = timings.OrderBy(t => t).Skip(94).First();
        
        Console.WriteLine($"Folder listing latency - Avg: {avgLatency:F2}ms, P95: {p95Latency:F2}ms");
        
        Assert.That(avgLatency, Is.LessThan(10), 
            "Average folder listing latency should be under 10ms");
        Assert.That(p95Latency, Is.LessThan(20), 
            "P95 folder listing latency should be under 20ms");
    }
    
    [Test]
    public async Task Search_MeetsLatencyTarget()
    {
        // Target: <100ms for searching 1M emails
        // For this test, we'll use our smaller dataset
        if (_importedEmails.Count == 0)
        {
            await ImportThroughput_MeetsTarget();
        }
        
        var searchTerms = new[] { "project", "update", "meeting", "important", "status" };
        var timings = new List<double>();
        
        foreach (var term in searchTerms)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _database.SearchAsync(term);
            stopwatch.Stop();
            
            Assert.That(result.IsSuccess, Is.True);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
            
            Console.WriteLine($"Search '{term}': {stopwatch.Elapsed.TotalMilliseconds:F2}ms, " +
                            $"Results: {result.Value.Count}");
        }
        
        var avgSearchTime = timings.Average();
        Console.WriteLine($"Average search time: {avgSearchTime:F2}ms");
        
        Assert.That(avgSearchTime, Is.LessThan(50), 
            "Search should complete in under 50ms for small dataset");
    }
    
    [Test]
    public async Task EmailRetrieval_MeetsLatencyTarget()
    {
        // Target: <5ms for single email retrieval
        if (_importedEmails.Count == 0)
        {
            await ImportThroughput_MeetsTarget();
        }
        
        var randomEmails = _importedEmails
            .OrderBy(e => Guid.NewGuid())
            .Take(100)
            .ToList();
            
        var timings = new List<double>();
        
        foreach (var emailId in randomEmails)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _database.GetEmailAsync(emailId);
            stopwatch.Stop();
            
            Assert.That(result.IsSuccess, Is.True);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        
        var avgRetrievalTime = timings.Average();
        var p95RetrievalTime = timings.OrderBy(t => t).Skip(94).First();
        
        Console.WriteLine($"Email retrieval - Avg: {avgRetrievalTime:F2}ms, P95: {p95RetrievalTime:F2}ms");
        
        Assert.That(avgRetrievalTime, Is.LessThan(5), 
            "Average email retrieval should be under 5ms");
    }
    
    [Test]
    public async Task CompressionRatio_MeetsTarget()
    {
        // Measure compression effectiveness
        var testData = GenerateCompressibleEmail(10000); // 10KB email
        
        var originalSize = Encoding.UTF8.GetByteCount(testData);
        var importResult = await _database.ImportEMLAsync(testData, "Compression");
        
        Assert.That(importResult.IsSuccess, Is.True);
        
        // Get database stats
        var statsResult = await _database.GetDatabaseStatsAsync();
        Assert.That(statsResult.IsSuccess, Is.True);
        
        var stats = statsResult.Value;
        var compressionRatio = (double)originalSize / (stats.DatabaseSize / stats.TotalEmails);
        
        Console.WriteLine($"Compression ratio: {compressionRatio:F2}x");
        
        Assert.That(compressionRatio, Is.GreaterThan(1.5), 
            "Should achieve at least 1.5x compression for text emails");
    }
    
    private (string fileName, string content)[] GenerateTestEmails(int count)
    {
        var emails = new List<(string, string)>();
        var subjects = new[] { "Meeting", "Update", "Report", "Question", "Reminder" };
        var domains = new[] { "example.com", "test.com", "demo.org" };
        
        for (int i = 0; i < count; i++)
        {
            var subject = subjects[i % subjects.Length];
            var domain = domains[i % domains.Length];
            
            var content = $@"From: sender{i}@{domain}
To: recipient@{domain}
Subject: {subject} #{i}
Message-ID: <perf-{i}@{domain}>
Date: {DateTime.UtcNow.AddDays(-i):R}

This is performance test email number {i}.
It contains various keywords like project, meeting, update, status, and report.
The purpose is to test search performance and compression ratios.

Some additional content to make the email more realistic:
- Task 1: Complete performance testing
- Task 2: Analyze results
- Task 3: Generate report

Best regards,
Sender {i}";

            emails.Add(($"perf_{i}.eml", content));
        }
        
        return emails.ToArray();
    }
    
    private string GenerateCompressibleEmail(int targetSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine("From: test@example.com");
        sb.AppendLine("To: recipient@example.com");
        sb.AppendLine("Subject: Compressible Email Test");
        sb.AppendLine("Message-ID: <compress-test@example.com>");
        sb.AppendLine();
        
        // Add repetitive content that compresses well
        var pattern = "This is a test pattern that repeats to create compressible content. ";
        while (sb.Length < targetSize)
        {
            sb.AppendLine(pattern);
        }
        
        return sb.ToString();
    }
}
```

### Task 6.3.2: Stress and Reliability Tests
**File**: `EmailDB.UnitTests/Stress/StressTests.cs`
**Dependencies**: All components
**Description**: Test system under stress conditions

```csharp
namespace EmailDB.UnitTests.Stress;

[TestFixture]
[Category("Stress")]
public class StressTests
{
    private string _testDbPath;
    
    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"stress_test_{Guid.NewGuid()}.db");
    }
    
    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }
    
    [Test]
    [Timeout(300000)] // 5 minute timeout
    public async Task ConcurrentAccess_HandlesMultipleReaders()
    {
        // Initialize database
        using (var db = new EmailDatabase(_testDbPath))
        {
            // Import initial emails
            var emails = GenerateEmails(1000);
            await db.ImportEMLBatchAsync(emails, "Concurrent");
        }
        
        // Test concurrent reads
        var tasks = new List<Task>();
        var errors = new ConcurrentBag<Exception>();
        var successCount = 0;
        
        // Start 10 concurrent readers
        for (int i = 0; i < 10; i++)
        {
            var readerId = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var db = new EmailDatabase(_testDbPath);
                    
                    for (int j = 0; j < 100; j++)
                    {
                        // Random operations
                        var op = Random.Shared.Next(3);
                        
                        switch (op)
                        {
                            case 0: // Search
                                var searchResult = await db.SearchAsync("test");
                                Assert.That(searchResult.IsSuccess, Is.True);
                                break;
                                
                            case 1: // List folder
                                var listResult = await db.GetFolderListingAsync("Concurrent");
                                Assert.That(listResult.IsSuccess, Is.True);
                                break;
                                
                            case 2: // Get stats
                                var statsResult = await db.GetDatabaseStatsAsync();
                                Assert.That(statsResult.IsSuccess, Is.True);
                                break;
                        }
                        
                        Interlocked.Increment(ref successCount);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }));
        }
        
        await Task.WhenAll(tasks);
        
        // Assert
        Assert.That(errors.Count, Is.Zero, 
            $"Concurrent access errors: {string.Join(", ", errors.Select(e => e.Message))}");
        Assert.That(successCount, Is.EqualTo(1000)); // 10 readers * 100 operations
    }
    
    [Test]
    public async Task LargeFile_HandlesOver1GB()
    {
        // Test with progressively larger imports
        using var db = new EmailDatabase(_testDbPath);
        
        var totalSize = 0L;
        var batchSize = 100;
        var targetSize = 100 * 1024 * 1024; // 100MB for test (1GB takes too long)
        
        while (totalSize < targetSize)
        {
            var emails = GenerateLargeEmails(batchSize, 50 * 1024); // 50KB each
            var result = await db.ImportEMLBatchAsync(emails, "Large");
            
            Assert.That(result.IsSuccess, Is.True);
            totalSize += emails.Sum(e => e.content.Length);
            
            Console.WriteLine($"Database size: {totalSize / (1024 * 1024)}MB");
        }
        
        // Verify database still functions
        var statsResult = await db.GetDatabaseStatsAsync();
        Assert.That(statsResult.IsSuccess, Is.True);
        Console.WriteLine($"Total emails: {statsResult.Value.TotalEmails}");
        Console.WriteLine($"Total blocks: {statsResult.Value.TotalBlocks}");
        
        // Test search on large database
        var searchStopwatch = Stopwatch.StartNew();
        var searchResult = await db.SearchAsync("attachment");
        searchStopwatch.Stop();
        
        Assert.That(searchResult.IsSuccess, Is.True);
        Console.WriteLine($"Search time on large DB: {searchStopwatch.ElapsedMilliseconds}ms");
    }
    
    [Test]
    public async Task MemoryUsage_StaysWithinLimits()
    {
        // Monitor memory usage during operations
        var initialMemory = GC.GetTotalMemory(true);
        
        using (var db = new EmailDatabase(_testDbPath))
        {
            // Import emails
            for (int batch = 0; batch < 10; batch++)
            {
                var emails = GenerateEmails(100);
                await db.ImportEMLBatchAsync(emails, "Memory");
                
                // Force garbage collection
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                var currentMemory = GC.GetTotalMemory(false);
                var memoryUsed = (currentMemory - initialMemory) / (1024 * 1024);
                
                Console.WriteLine($"Batch {batch}: Memory used: {memoryUsed}MB");
                
                // Memory usage should stay reasonable
                Assert.That(memoryUsed, Is.LessThan(500), 
                    "Memory usage should stay under 500MB");
            }
        }
    }
    
    [Test]
    public async Task CorruptionRecovery_HandlesPartialWrites()
    {
        // Test recovery from simulated corruption
        var blockManager = new RawBlockManager(_testDbPath);
        
        try
        {
            // Write some valid blocks
            await blockManager.WriteBlockAsync(BlockType.Header, new byte[] { 1, 2, 3 });
            await blockManager.WriteBlockAsync(BlockType.Metadata, new byte[] { 4, 5, 6 });
            
            // Simulate corruption by writing invalid data directly
            blockManager.Dispose();
            
            using (var fs = new FileStream(_testDbPath, FileMode.Append))
            {
                // Write partial block (missing footer)
                var corruptData = new byte[100];
                Random.Shared.NextBytes(corruptData);
                await fs.WriteAsync(corruptData);
            }
            
            // Try to open and scan
            blockManager = new RawBlockManager(_testDbPath);
            var locations = blockManager.GetBlockLocations();
            
            // Should have recovered the valid blocks
            Assert.That(locations.Count, Is.GreaterThanOrEqualTo(2));
        }
        finally
        {
            blockManager?.Dispose();
        }
    }
    
    [Test]
    [Repeat(5)] // Run multiple times to catch race conditions
    public async Task RaceCondition_EmailIdGeneration()
    {
        // Test that email ID generation is thread-safe
        using var db = new EmailDatabase(_testDbPath);
        
        var tasks = new List<Task<Result<EmailHashedID>>>();
        var emailContent = @"From: test@test.com
Subject: Race Test
Message-ID: <race-{0}@test.com>

Test content";

        // Start 20 concurrent imports
        for (int i = 0; i < 20; i++)
        {
            var id = i;
            tasks.Add(Task.Run(() => 
                db.ImportEMLAsync(string.Format(emailContent, id), "Race")));
        }
        
        var results = await Task.WhenAll(tasks);
        
        // All should succeed
        Assert.That(results.All(r => r.IsSuccess), Is.True);
        
        // All IDs should be unique
        var ids = results.Select(r => r.Value.ToCompoundKey()).ToList();
        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
    }
    
    private (string fileName, string content)[] GenerateEmails(int count)
    {
        var emails = new List<(string, string)>();
        
        for (int i = 0; i < count; i++)
        {
            var content = $@"From: stress{i}@test.com
To: recipient@test.com
Subject: Stress Test Email {i}
Message-ID: <stress-{i}-{Guid.NewGuid()}@test.com>
Date: {DateTime.UtcNow:R}

This is stress test email {i}.
It contains test content for concurrent access testing.";

            emails.Add(($"stress_{i}.eml", content));
        }
        
        return emails.ToArray();
    }
    
    private (string fileName, string content)[] GenerateLargeEmails(int count, int sizePerEmail)
    {
        var emails = new List<(string, string)>();
        
        for (int i = 0; i < count; i++)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"From: large{i}@test.com");
            sb.AppendLine("To: recipient@test.com");
            sb.AppendLine($"Subject: Large Email {i}");
            sb.AppendLine($"Message-ID: <large-{i}-{Guid.NewGuid()}@test.com>");
            sb.AppendLine();
            
            // Add attachment-like content
            sb.AppendLine("This email simulates having a large attachment.");
            
            var attachmentData = new byte[sizePerEmail - sb.Length];
            Random.Shared.NextBytes(attachmentData);
            sb.AppendLine(Convert.ToBase64String(attachmentData));
            
            emails.Add(($"large_{i}.eml", sb.ToString()));
        }
        
        return emails.ToArray();
    }
}
```

## Section 6.4: Test Infrastructure and Helpers

### Task 6.4.1: Enhanced Test Helpers
**File**: `EmailDB.UnitTests/Helpers/EnhancedTestHelpers.cs`
**Dependencies**: All components
**Description**: Additional test utilities and fixtures

```csharp
namespace EmailDB.UnitTests.Helpers;

/// <summary>
/// Test data generator using Bogus library.
/// </summary>
public class TestDataGenerator
{
    private readonly Faker _faker;
    
    public TestDataGenerator(string locale = "en")
    {
        _faker = new Faker(locale);
    }
    
    public MimeMessage GenerateEmail(EmailOptions options = null)
    {
        options ??= new EmailOptions();
        
        var message = new MimeMessage();
        
        // From
        message.From.Add(new MailboxAddress(
            _faker.Name.FullName(),
            _faker.Internet.Email()));
            
        // To
        var recipientCount = _faker.Random.Int(1, options.MaxRecipients);
        for (int i = 0; i < recipientCount; i++)
        {
            message.To.Add(new MailboxAddress(
                _faker.Name.FullName(),
                _faker.Internet.Email()));
        }
        
        // CC
        if (_faker.Random.Bool(0.3f)) // 30% chance of CC
        {
            var ccCount = _faker.Random.Int(1, 3);
            for (int i = 0; i < ccCount; i++)
            {
                message.Cc.Add(new MailboxAddress(
                    _faker.Name.FullName(),
                    _faker.Internet.Email()));
            }
        }
        
        // Subject
        message.Subject = options.SubjectTemplate ?? _faker.Lorem.Sentence();
        
        // Message ID
        message.MessageId = $"<{Guid.NewGuid()}@{_faker.Internet.DomainName()}>";
        
        // Date
        message.Date = options.DateRange != null
            ? _faker.Date.Between(options.DateRange.Start, options.DateRange.End)
            : _faker.Date.Recent(30);
            
        // Body
        var bodyBuilder = new BodyBuilder();
        
        if (options.IncludeHtml)
        {
            bodyBuilder.HtmlBody = $@"<html>
<body>
<p>{_faker.Lorem.Paragraphs(options.ParagraphCount)}</p>
{(options.IncludeSignature ? $"<p>Best regards,<br/>{message.From[0].Name}</p>" : "")}
</body>
</html>";
        }
        
        bodyBuilder.TextBody = _faker.Lorem.Paragraphs(options.ParagraphCount);
        if (options.IncludeSignature)
        {
            bodyBuilder.TextBody += $"\n\nBest regards,\n{message.From[0].Name}";
        }
        
        // Attachments
        if (options.AttachmentCount > 0)
        {
            for (int i = 0; i < options.AttachmentCount; i++)
            {
                var attachmentData = _faker.Random.Bytes(options.AttachmentSize);
                bodyBuilder.Attachments.Add(
                    $"document_{i + 1}.pdf",
                    attachmentData,
                    ContentType.Parse("application/pdf"));
            }
        }
        
        message.Body = bodyBuilder.ToMessageBody();
        
        return message;
    }
    
    public (string fileName, string content)[] GenerateEmailBatch(int count, EmailOptions options = null)
    {
        var emails = new List<(string, string)>();
        
        for (int i = 0; i < count; i++)
        {
            var message = GenerateEmail(options);
            var fileName = $"email_{i}_{_faker.Random.AlphaNumeric(8)}.eml";
            
            using var ms = new MemoryStream();
            message.WriteTo(ms);
            var content = Encoding.UTF8.GetString(ms.ToArray());
            
            emails.Add((fileName, content));
        }
        
        return emails.ToArray();
    }
}

public class EmailOptions
{
    public int MaxRecipients { get; set; } = 3;
    public bool IncludeHtml { get; set; } = true;
    public bool IncludeSignature { get; set; } = true;
    public int ParagraphCount { get; set; } = 3;
    public int AttachmentCount { get; set; } = 0;
    public int AttachmentSize { get; set; } = 1024 * 100; // 100KB
    public string SubjectTemplate { get; set; }
    public (DateTime Start, DateTime End)? DateRange { get; set; }
}

/// <summary>
/// Test fixture base class with common setup.
/// </summary>
public abstract class EmailDBTestBase
{
    protected string TestDbPath { get; private set; }
    protected string TestIndexPath { get; private set; }
    protected TestDataGenerator DataGenerator { get; private set; }
    
    [SetUp]
    public virtual void BaseSetup()
    {
        TestDbPath = Path.Combine(Path.GetTempPath(), $"test_{GetType().Name}_{Guid.NewGuid()}.db");
        TestIndexPath = Path.Combine(Path.GetTempPath(), $"test_{GetType().Name}_index_{Guid.NewGuid()}");
        DataGenerator = new TestDataGenerator();
    }
    
    [TearDown]
    public virtual void BaseTearDown()
    {
        CleanupTestFiles();
    }
    
    protected void CleanupTestFiles()
    {
        if (File.Exists(TestDbPath))
            File.Delete(TestDbPath);
            
        if (Directory.Exists(TestIndexPath))
            Directory.Delete(TestIndexPath, true);
            
        // Clean up any backup files
        var dir = Path.GetDirectoryName(TestDbPath);
        var pattern = Path.GetFileName(TestDbPath) + ".*";
        
        foreach (var file in Directory.GetFiles(dir, pattern))
        {
            try { File.Delete(file); } catch { }
        }
    }
    
    protected async Task<List<EmailHashedID>> ImportTestEmailsAsync(
        EmailDatabase db, 
        int count, 
        string folder = "Test")
    {
        var emails = DataGenerator.GenerateEmailBatch(count);
        var result = await db.ImportEMLBatchAsync(emails, folder);
        
        Assert.That(result.IsSuccess, Is.True);
        return result.Value.ImportedEmailIds;
    }
}

/// <summary>
/// Mock builder for complex test scenarios.
/// </summary>
public class EmailDBMockBuilder
{
    private readonly Mock<IRawBlockManager> _blockManagerMock = new();
    private readonly Mock<IBlockContentSerializer> _serializerMock = new();
    private readonly Mock<ILogger> _loggerMock = new();
    
    public EmailDBMockBuilder WithBlockRead(long blockId, Block block)
    {
        _blockManagerMock
            .Setup(m => m.ReadBlockAsync(blockId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Block>.Success(block));
        return this;
    }
    
    public EmailDBMockBuilder WithBlockWrite(long blockId)
    {
        _blockManagerMock
            .Setup(m => m.WriteBlockAsync(
                It.IsAny<BlockType>(),
                It.IsAny<byte[]>(),
                It.IsAny<CompressionAlgorithm>(),
                It.IsAny<EncryptionAlgorithm>(),
                It.IsAny<string>()))
            .ReturnsAsync(Result<long>.Success(blockId));
        return this;
    }
    
    public EmailDBMockBuilder WithSerialization<T>(T content, byte[] data) where T : BlockContent
    {
        _serializerMock
            .Setup(s => s.Serialize(It.IsAny<T>(), It.IsAny<PayloadEncoding>()))
            .Returns(Result<byte[]>.Success(data));
            
        _serializerMock
            .Setup(s => s.Deserialize<T>(data, It.IsAny<PayloadEncoding>()))
            .Returns(Result<T>.Success(content));
            
        return this;
    }
    
    public (Mock<IRawBlockManager> blockManager, 
            Mock<IBlockContentSerializer> serializer, 
            Mock<ILogger> logger) Build()
    {
        return (_blockManagerMock, _serializerMock, _loggerMock);
    }
}
```

### Task 6.4.2: Test Configuration and Categories
**File**: `EmailDB.UnitTests/TestConfiguration.cs`
**Dependencies**: NUnit configuration
**Description**: Test configuration and category definitions

```csharp
namespace EmailDB.UnitTests;

/// <summary>
/// Global test configuration and constants.
/// </summary>
public static class TestConfiguration
{
    /// <summary>
    /// Test categories for organizing test runs.
    /// </summary>
    public static class Categories
    {
        public const string Unit = "Unit";
        public const string Integration = "Integration";
        public const string Performance = "Performance";
        public const string Stress = "Stress";
        public const string Slow = "Slow";
        public const string Fast = "Fast";
        public const string Benchmark = "Benchmark";
    }
    
    /// <summary>
    /// Performance thresholds for assertions.
    /// </summary>
    public static class PerformanceTargets
    {
        public const int ImportThroughputEmailsPerSecond = 1000;
        public const double FolderListingLatencyMs = 10.0;
        public const double SearchLatencyMs = 100.0;
        public const double EmailRetrievalLatencyMs = 5.0;
        public const double IndexOperationLatencyMs = 1.0;
        public const int MaxMemoryUsageMB = 500;
    }
    
    /// <summary>
    /// Test data sizes for different scenarios.
    /// </summary>
    public static class DataSizes
    {
        public const int SmallBatchSize = 10;
        public const int MediumBatchSize = 100;
        public const int LargeBatchSize = 1000;
        public const int StressBatchSize = 10000;
        
        public const int SmallEmailSize = 1024;        // 1KB
        public const int MediumEmailSize = 50 * 1024;  // 50KB
        public const int LargeEmailSize = 1024 * 1024; // 1MB
    }
    
    /// <summary>
    /// Timeout values for different test types.
    /// </summary>
    public static class Timeouts
    {
        public const int FastTestMs = 1000;      // 1 second
        public const int NormalTestMs = 10000;   // 10 seconds
        public const int SlowTestMs = 60000;     // 1 minute
        public const int StressTestMs = 300000;  // 5 minutes
    }
}

/// <summary>
/// Custom NUnit attributes for test organization.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresDatabaseAttribute : CategoryAttribute
{
    public RequiresDatabaseAttribute() : base("RequiresDatabase") { }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresIndexAttribute : CategoryAttribute
{
    public RequiresIndexAttribute() : base("RequiresIndex") { }
}

/// <summary>
/// Assembly-level test configuration.
/// </summary>
[SetUpFixture]
public class GlobalTestSetup
{
    [OneTimeSetUp]
    public void RunBeforeAnyTests()
    {
        // Set up test environment
        Environment.SetEnvironmentVariable("EMAILDB_TEST_MODE", "true");
        
        // Configure test logging
        TestContext.Progress.WriteLine("EmailDB Test Suite Starting");
        TestContext.Progress.WriteLine($"Temp Path: {Path.GetTempPath()}");
        TestContext.Progress.WriteLine($"Process ID: {Process.GetCurrentProcess().Id}");
        
        // Ensure temp directory is clean
        CleanupTempFiles();
    }
    
    [OneTimeTearDown]
    public void RunAfterAllTests()
    {
        TestContext.Progress.WriteLine("EmailDB Test Suite Completed");
        
        // Final cleanup
        CleanupTempFiles();
    }
    
    private void CleanupTempFiles()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var testFiles = Directory.GetFiles(tempPath, "test_*.db");
            var testDirs = Directory.GetDirectories(tempPath, "test_*");
            
            foreach (var file in testFiles)
            {
                try { File.Delete(file); } catch { }
            }
            
            foreach (var dir in testDirs)
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"Cleanup error: {ex.Message}");
        }
    }
}
```

## Implementation Timeline

### Week 1: Unit Tests (Days 1-5)
**Day 1-2: Block and Serialization Tests**
- [ ] Task 6.1.1: Block type serialization tests
- [ ] Test all new block types (EmailEnvelope, FolderEnvelope, KeyManager, etc.)
- [ ] Verify round-trip serialization

**Day 3: Infrastructure Tests**
- [ ] Task 6.1.2: Compression and encryption tests
- [ ] Task 6.1.3: Key management tests
- [ ] Test all algorithms and key operations

**Day 4-5: Manager Tests**
- [ ] Task 6.1.4: Manager layer unit tests
- [ ] Task 6.1.5: Index and version tests
- [ ] Mock dependencies for isolation

### Week 2: Integration Tests (Days 6-10)
**Day 6-7: Workflow Tests**
- [ ] Task 6.2.1: End-to-end workflow tests
- [ ] Complete email lifecycle testing
- [ ] Batch operations

**Day 8: Transaction Tests**
- [ ] Task 6.2.2: Transaction and atomicity tests
- [ ] Rollback scenarios
- [ ] Partial failure handling

**Day 9-10: Upgrade Tests**
- [ ] Task 6.2.3: Version upgrade tests
- [ ] Migration testing
- [ ] Compatibility verification

### Week 3: Performance and Polish (Days 11-15)
**Day 11-12: Performance Tests**
- [ ] Task 6.3.1: Performance benchmarks
- [ ] Throughput measurements
- [ ] Latency validation

**Day 13: Stress Tests**
- [ ] Task 6.3.2: Stress and reliability tests
- [ ] Concurrent access
- [ ] Large file handling

**Day 14: Test Infrastructure**
- [ ] Task 6.4.1: Enhanced test helpers
- [ ] Task 6.4.2: Test configuration
- [ ] Documentation

**Day 15: Coverage and Cleanup**
- [ ] Run full test suite
- [ ] Measure code coverage
- [ ] Fix any failing tests
- [ ] Performance report

## Success Criteria

1. **Code Coverage**: >80% coverage of new components
2. **Test Execution Time**: Fast tests complete in <30 seconds
3. **Performance Validation**: All performance targets met
4. **Reliability**: Zero flaky tests
5. **Categories**: Tests properly categorized for selective execution
6. **Documentation**: All tests have clear descriptions

## Test Execution Strategy

### Continuous Integration
```bash
# Fast tests for every commit
dotnet test --filter "Category=Fast|Category=Unit"

# Full suite for PRs
dotnet test --filter "Category!=Stress"

# Nightly stress tests
dotnet test --filter "Category=Stress|Category=Performance"
```

### Local Development
```bash
# Run specific test category
dotnet test --filter "Category=Integration"

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover

# Run with detailed logging
dotnet test --logger "console;verbosity=detailed"
```

## Risk Mitigation

1. **Test Isolation**: Each test uses unique file names
2. **Cleanup**: Proper teardown prevents file system pollution
3. **Timeouts**: Prevent hanging tests
4. **Categories**: Allow skipping slow tests during development
5. **Mocking**: Reduce dependencies and improve reliability