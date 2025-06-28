# Phase 1 Implementation Plan ✅ COMPLETED

## Overview
This document provides a detailed implementation plan for Phase 1 of the EmailDB refactoring project. The goal is to build the foundational infrastructure for the new HybridEmailStore architecture while maximizing reuse of existing, tested code.

**Status: COMPLETED** - All Phase 1 components have been successfully implemented and tested.

## Class Hierarchy and Architecture

### Core Abstractions Layer
```
IBlockContent (existing interface)
├── EmailEnvelope (new)
├── FolderEnvelopeBlock (new)
├── KeyManagerContent (new)
├── KeyExchangeContent (new)
└── [Existing: MetadataContent, FolderContent, SegmentContent, etc.]

IPayloadEncoding (existing interface)
├── ProtobufPayloadEncoding (enhance existing)
├── JsonPayloadEncoding (existing)
└── RawBytesPayloadEncoding (existing)
```

### Storage Infrastructure Layer
```
RawBlockManager (existing - minimal changes)
├── BlockProcessor (new) - Handles compression/encryption pipeline
├── ExtendedHeaderManager (new) - Manages variable-length headers
└── BlockIndexManager (new) - Persistent block index

EmailStorageManager (new)
├── EmailBlockBuilder - Batches emails into blocks
├── AdaptiveBlockSizer - Determines optimal block sizes
└── EmailDeduplicationService - Prevents duplicate storage
```

### Security Infrastructure Layer
```
AlgorithmRegistry (new)
├── Compression Providers
│   ├── LZ4CompressionProvider
│   ├── GzipCompressionProvider
│   ├── ZstdCompressionProvider
│   └── BrotliCompressionProvider
└── Encryption Providers
    ├── AES256GcmProvider
    ├── ChaCha20Poly1305Provider
    └── AES256CbcHmacProvider

BlockKeyManager (new)
├── KeyExchangeRegistry
│   ├── PasswordKeyExchangeProvider
│   ├── WebAuthnKeyExchangeProvider
│   └── PGPKeyExchangeProvider
└── MasterKeyManager
```

## Implementation Details

## Section 1.1: Block Type Definitions

### Task 1.1.1: Create Email Envelope Model
**File**: `EmailDB.Format/Models/EmailContent/EmailEnvelope.cs`
**Dependencies**: None
**Description**: Lightweight email metadata for fast folder listings

```csharp
namespace EmailDB.Format.Models.EmailContent;

[ProtoContract]
public class EmailEnvelope
{
    [ProtoMember(1)]
    public string CompoundId { get; set; } // BlockId:LocalId
    
    [ProtoMember(2)]
    public string MessageId { get; set; }
    
    [ProtoMember(3)]
    public string Subject { get; set; }
    
    [ProtoMember(4)]
    public string From { get; set; }
    
    [ProtoMember(5)]
    public string To { get; set; }
    
    [ProtoMember(6)]
    public DateTime Date { get; set; }
    
    [ProtoMember(7)]
    public long Size { get; set; }
    
    [ProtoMember(8)]
    public bool HasAttachments { get; set; }
    
    [ProtoMember(9)]
    public int Flags { get; set; } // Read, Flagged, etc.
    
    [ProtoMember(10)]
    public byte[] EnvelopeHash { get; set; }
}
```

### Task 1.1.2: Create Folder Envelope Block
**File**: `EmailDB.Format/Models/BlockTypes/FolderEnvelopeBlock.cs`
**Dependencies**: EmailEnvelope, BlockContent
**Description**: Contains all envelopes for emails in a folder

```csharp
namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class FolderEnvelopeBlock : BlockContent
{
    [ProtoMember(1)]
    public string FolderPath { get; set; }
    
    [ProtoMember(2)]
    public int Version { get; set; }
    
    [ProtoMember(3)]
    public List<EmailEnvelope> Envelopes { get; set; } = new();
    
    [ProtoMember(4)]
    public DateTime LastModified { get; set; }
    
    [ProtoMember(5)]
    public long? PreviousBlockId { get; set; } // For versioning
    
    public override BlockType GetBlockType() => BlockType.FolderEnvelope;
}
```

### Task 1.1.3: Update Existing Block Types
**Files**: 
- `EmailDB.Format/Models/BlockTypes/FolderContent.cs`
- `EmailDB.Format/Models/BlockType.cs`
**Dependencies**: None
**Description**: Add new fields to support envelope blocks and versioning

```csharp
// Add to FolderContent.cs
[ProtoMember(10)]
public long EnvelopeBlockId { get; set; }

[ProtoMember(11)]
public int Version { get; set; }

[ProtoMember(12)]
public DateTime LastModified { get; set; }

// Add to BlockType enum
public enum BlockType : int
{
    // ... existing types ...
    FolderEnvelope = 8,
    EmailBatch = 9,
    KeyManager = 10,
    KeyExchange = 11
}
```

### Task 1.1.4: Create Missing ZoneTree Block Types
**Files**:
- `EmailDB.Format/Models/BlockTypes/ZoneTreeSegmentKVContent.cs`
- `EmailDB.Format/Models/BlockTypes/ZoneTreeSegmentVectorContent.cs`
**Dependencies**: BlockContent
**Description**: Enable ZoneTree to store its data in EmailDB blocks

```csharp
[ProtoContract]
public class ZoneTreeSegmentKVContent : BlockContent
{
    [ProtoMember(1)]
    public byte[] KeyValueData { get; set; }
    
    [ProtoMember(2)]
    public string SegmentId { get; set; }
    
    [ProtoMember(3)]
    public int Version { get; set; }
    
    public override BlockType GetBlockType() => BlockType.ZoneTreeSegment_KV;
}
```

## Section 1.2: Email Storage and Batching

### Task 1.2.1: Implement Email ID System
**File**: `EmailDB.Format/Models/EmailContent/EmailHashedID.cs`
**Dependencies**: MimeKit
**Description**: Collision-resistant email identification system

```csharp
namespace EmailDB.Format.Models.EmailContent;

public class EmailHashedID
{
    public long BlockId { get; set; }
    public int LocalId { get; set; }
    public byte[] EnvelopeHash { get; set; }
    public byte[] ContentHash { get; set; }
    
    public string ToCompoundKey() => $"{BlockId}:{LocalId}";
    
    public static EmailHashedID FromCompoundKey(string key)
    {
        var parts = key.Split(':');
        return new EmailHashedID 
        { 
            BlockId = long.Parse(parts[0]),
            LocalId = int.Parse(parts[1])
        };
    }
    
    public static byte[] ComputeEnvelopeHash(MimeMessage message)
    {
        var hashInput = new StringBuilder();
        
        // Primary fields
        hashInput.Append(message.MessageId ?? "");
        hashInput.Append("|");
        hashInput.Append(message.From?.ToString() ?? "");
        hashInput.Append("|");
        hashInput.Append(message.To?.ToString() ?? "");
        hashInput.Append("|");
        hashInput.Append(message.Date.ToString("O"));
        hashInput.Append("|");
        hashInput.Append(message.Subject ?? "");
        hashInput.Append("|");
        
        // Additional fields for collision prevention
        hashInput.Append(message.Cc?.ToString() ?? "");
        hashInput.Append("|");
        hashInput.Append(message.InReplyTo ?? "");
        hashInput.Append("|");
        hashInput.Append(message.References?.FirstOrDefault() ?? "");
        hashInput.Append("|");
        
        // Size as differentiator
        var size = Encoding.UTF8.GetByteCount(message.ToString());
        hashInput.Append(size);
        
        return SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()));
    }
    
    public static byte[] ComputeContentHash(byte[] emailData)
    {
        return SHA256.HashData(emailData);
    }
}
```

### Task 1.2.2: Create Adaptive Block Sizing
**File**: `EmailDB.Format/FileManagement/AdaptiveBlockSizer.cs`
**Dependencies**: None
**Description**: Determines optimal block sizes based on database size

```csharp
namespace EmailDB.Format.FileManagement;

public class AdaptiveBlockSizer
{
    private readonly (long dbSize, int blockSize)[] _sizeProgression = new[]
    {
        (5L * 1024 * 1024 * 1024,     50 * 1024 * 1024),   // < 5GB: 50MB blocks
        (25L * 1024 * 1024 * 1024,   100 * 1024 * 1024),   // < 25GB: 100MB blocks
        (100L * 1024 * 1024 * 1024,  250 * 1024 * 1024),   // < 100GB: 250MB blocks
        (500L * 1024 * 1024 * 1024,  500 * 1024 * 1024),   // < 500GB: 500MB blocks
        (long.MaxValue,             1024 * 1024 * 1024)     // >= 500GB: 1GB blocks
    };
    
    public int GetTargetBlockSize(long currentDatabaseSize)
    {
        foreach (var (threshold, size) in _sizeProgression)
        {
            if (currentDatabaseSize < threshold)
                return size;
        }
        return _sizeProgression[^1].blockSize;
    }
    
    public int GetTargetBlockSizeMB(long currentDatabaseSize)
    {
        return GetTargetBlockSize(currentDatabaseSize) / (1024 * 1024);
    }
}
```

### Task 1.2.3: Implement Email Block Builder
**File**: `EmailDB.Format/FileManagement/EmailBlockBuilder.cs`
**Dependencies**: EmailHashedID, IBlockContentSerializer
**Description**: Batches multiple emails into single blocks

```csharp
namespace EmailDB.Format.FileManagement;

public class EmailEntry
{
    public MimeMessage Message { get; set; }
    public byte[] Data { get; set; }
    public int LocalId { get; set; }
    public byte[] EnvelopeHash { get; set; }
    public byte[] ContentHash { get; set; }
}

public class EmailBlockBuilder
{
    private readonly int _targetSize;
    private readonly List<EmailEntry> _pendingEmails = new();
    private int _currentSize = 0;
    
    public bool ShouldFlush => _currentSize >= _targetSize;
    public int CurrentSize => _currentSize;
    public int EmailCount => _pendingEmails.Count;
    public int TargetSize => _targetSize;
    
    public EmailBlockBuilder(int targetSizeMB)
    {
        _targetSize = targetSizeMB * 1024 * 1024;
    }
    
    public EmailEntry AddEmail(MimeMessage message, byte[] emailData)
    {
        var entry = new EmailEntry
        {
            Message = message,
            Data = emailData,
            LocalId = _pendingEmails.Count,
            EnvelopeHash = EmailHashedID.ComputeEnvelopeHash(message),
            ContentHash = EmailHashedID.ComputeContentHash(emailData)
        };
        
        _pendingEmails.Add(entry);
        _currentSize += emailData.Length;
        
        return entry;
    }
    
    public byte[] SerializeBlock()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Write email count
        writer.Write(_pendingEmails.Count);
        
        // Write table of contents
        var offsets = new List<long>();
        foreach (var email in _pendingEmails)
        {
            offsets.Add(ms.Position);
            writer.Write(email.Data.Length);
            writer.Write(email.EnvelopeHash);
            writer.Write(email.ContentHash);
        }
        
        // Write email data
        foreach (var email in _pendingEmails)
        {
            writer.Write(email.Data);
        }
        
        return ms.ToArray();
    }
    
    public List<EmailEntry> GetPendingEmails() => _pendingEmails.ToList();
    
    public void Clear()
    {
        _pendingEmails.Clear();
        _currentSize = 0;
    }
}
```

### Task 1.2.4: Create Email Storage Manager
**File**: `EmailDB.Format/FileManagement/EmailStorageManager.cs`
**Dependencies**: RawBlockManager, AdaptiveBlockSizer, EmailBlockBuilder, ZoneTree
**Description**: Coordinates email storage with deduplication and indexing

```csharp
namespace EmailDB.Format.FileManagement;

public class EmailStorageManager
{
    private readonly RawBlockManager _blockManager;
    private readonly AdaptiveBlockSizer _sizer;
    private readonly IZoneTree<string, string> _envelopeHashIndex;
    private readonly IZoneTree<string, string> _contentHashIndex;
    private readonly IZoneTree<string, string> _messageIdIndex;
    private EmailBlockBuilder _currentBuilder;
    private long _databaseSize;
    
    public EmailStorageManager(
        RawBlockManager blockManager,
        IZoneTree<string, string> envelopeHashIndex,
        IZoneTree<string, string> contentHashIndex,
        IZoneTree<string, string> messageIdIndex)
    {
        _blockManager = blockManager;
        _sizer = new AdaptiveBlockSizer();
        _envelopeHashIndex = envelopeHashIndex;
        _contentHashIndex = contentHashIndex;
        _messageIdIndex = messageIdIndex;
    }
    
    public async Task<Result<EmailHashedID>> StoreEmailAsync(
        MimeMessage message, 
        byte[] emailData)
    {
        // Check for duplicates
        var envelopeHash = EmailHashedID.ComputeEnvelopeHash(message);
        var existingId = await CheckDuplicateAsync(envelopeHash);
        if (existingId != null)
            return Result<EmailHashedID>.Success(existingId);
        
        // Get appropriate block size
        var targetSizeMB = _sizer.GetTargetBlockSizeMB(_databaseSize);
        
        // Initialize builder if needed
        if (_currentBuilder == null || _currentBuilder.TargetSize != targetSizeMB * 1024 * 1024)
        {
            if (_currentBuilder?.EmailCount > 0)
                await FlushCurrentBlockAsync();
                
            _currentBuilder = new EmailBlockBuilder(targetSizeMB);
        }
        
        // Add email to builder
        var entry = _currentBuilder.AddEmail(message, emailData);
        
        // Create pending ID
        var pendingId = new EmailHashedID
        {
            LocalId = entry.LocalId,
            EnvelopeHash = entry.EnvelopeHash,
            ContentHash = entry.ContentHash
            // BlockId will be set on flush
        };
        
        // Flush if needed
        if (_currentBuilder.ShouldFlush)
        {
            await FlushCurrentBlockAsync();
        }
        
        return Result<EmailHashedID>.Success(pendingId);
    }
    
    private async Task<EmailHashedID> CheckDuplicateAsync(byte[] envelopeHash)
    {
        var hashKey = Convert.ToBase64String(envelopeHash);
        
        if (_envelopeHashIndex.TryGet(hashKey, out var compoundKey))
        {
            return EmailHashedID.FromCompoundKey(compoundKey);
        }
        
        return null;
    }
    
    private async Task<Result<long>> FlushCurrentBlockAsync()
    {
        if (_currentBuilder == null || _currentBuilder.EmailCount == 0)
            return Result<long>.Failure("No emails to flush");
        
        // Serialize block
        var blockData = _currentBuilder.SerializeBlock();
        
        // Write block with compression
        var blockIdResult = await _blockManager.WriteBlockAsync(
            BlockType.EmailBatch,
            blockData,
            compression: CompressionAlgorithm.LZ4);
            
        if (!blockIdResult.IsSuccess)
            return blockIdResult;
            
        var blockId = blockIdResult.Value;
        
        // Update indexes for all emails in block
        foreach (var email in _currentBuilder.GetPendingEmails())
        {
            var compoundKey = $"{blockId}:{email.LocalId}";
            
            // Update all indexes
            _envelopeHashIndex.Upsert(
                Convert.ToBase64String(email.EnvelopeHash), 
                compoundKey);
            _contentHashIndex.Upsert(
                Convert.ToBase64String(email.ContentHash), 
                compoundKey);
            _messageIdIndex.Upsert(
                email.Message.MessageId, 
                compoundKey);
        }
        
        // Update database size
        _databaseSize += _currentBuilder.CurrentSize;
        
        // Clear builder
        _currentBuilder.Clear();
        
        return Result<long>.Success(blockId);
    }
    
    public async Task<Result> FlushPendingEmailsAsync()
    {
        if (_currentBuilder?.EmailCount > 0)
        {
            var result = await FlushCurrentBlockAsync();
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        return Result.Success();
    }
}
```

## Section 1.3: Serialization Infrastructure

### Task 1.3.1: Update DefaultBlockContentSerializer
**File**: `EmailDB.Format/Helpers/DefaultBlockContentSerializer.cs`
**Dependencies**: IPayloadEncoding
**Description**: Refactor to use IPayloadEncoding interface

```csharp
public class DefaultBlockContentSerializer : IBlockContentSerializer
{
    private readonly Dictionary<PayloadEncoding, IPayloadEncoding> _encodings;
    
    public DefaultBlockContentSerializer()
    {
        _encodings = new Dictionary<PayloadEncoding, IPayloadEncoding>
        {
            { PayloadEncoding.Protobuf, new ProtobufPayloadEncoding() },
            { PayloadEncoding.Json, new JsonPayloadEncoding() },
            { PayloadEncoding.RawBytes, new RawBytesPayloadEncoding() }
        };
    }
    
    public Result<byte[]> Serialize<T>(T content, PayloadEncoding encoding) where T : class
    {
        if (!_encodings.TryGetValue(encoding, out var encoder))
            return Result<byte[]>.Failure($"Unsupported encoding: {encoding}");
            
        return encoder.Encode(content);
    }
    
    public Result<T> Deserialize<T>(byte[] data, PayloadEncoding encoding) where T : class
    {
        if (!_encodings.TryGetValue(encoding, out var encoder))
            return Result<T>.Failure($"Unsupported encoding: {encoding}");
            
        return encoder.Decode<T>(data);
    }
}
```

## Section 1.4: Compression and Encryption Infrastructure

### Task 1.4.1: Define Block Flags
**File**: `EmailDB.Format/Models/BlockFlags.cs`
**Dependencies**: None
**Description**: Utilize existing Flags field for compression/encryption metadata

```csharp
namespace EmailDB.Format.Models;

[Flags]
public enum BlockFlags : uint
{
    None            = 0x00000000,
    
    // Compression (bits 0-7)
    Compressed      = 0x00000001,
    CompressionMask = 0x000000FE,  // 7 bits for algorithm ID
    
    // Encryption (bits 8-15)
    Encrypted       = 0x00000100,
    EncryptionMask  = 0x0000FE00,  // 7 bits for algorithm ID
    
    // Reserved for future use
    Reserved        = 0xFFFF0000
}

public enum CompressionAlgorithm : byte
{
    None = 0,
    Gzip = 1,
    LZ4 = 2,
    Zstd = 3,
    Brotli = 4
}

public enum EncryptionAlgorithm : byte
{
    None = 0,
    AES256_GCM = 1,
    ChaCha20_Poly1305 = 2,
    AES256_CBC_HMAC = 3
}

public static class BlockFlagsExtensions
{
    public static CompressionAlgorithm GetCompressionAlgorithm(this BlockFlags flags)
    {
        if ((flags & BlockFlags.Compressed) == 0)
            return CompressionAlgorithm.None;
            
        var id = (byte)((uint)(flags & BlockFlags.CompressionMask) >> 1);
        return (CompressionAlgorithm)id;
    }
    
    public static EncryptionAlgorithm GetEncryptionAlgorithm(this BlockFlags flags)
    {
        if ((flags & BlockFlags.Encrypted) == 0)
            return EncryptionAlgorithm.None;
            
        var id = (byte)((uint)(flags & BlockFlags.EncryptionMask) >> 9);
        return (EncryptionAlgorithm)id;
    }
    
    public static BlockFlags SetCompressionAlgorithm(
        this BlockFlags flags, 
        CompressionAlgorithm algorithm)
    {
        if (algorithm == CompressionAlgorithm.None)
            return flags & ~(BlockFlags.Compressed | BlockFlags.CompressionMask);
            
        flags |= BlockFlags.Compressed;
        flags &= ~BlockFlags.CompressionMask;
        flags |= (BlockFlags)((byte)algorithm << 1);
        return flags;
    }
    
    public static BlockFlags SetEncryptionAlgorithm(
        this BlockFlags flags, 
        EncryptionAlgorithm algorithm)
    {
        if (algorithm == EncryptionAlgorithm.None)
            return flags & ~(BlockFlags.Encrypted | BlockFlags.EncryptionMask);
            
        flags |= BlockFlags.Encrypted;
        flags &= ~BlockFlags.EncryptionMask;
        flags |= (BlockFlags)((byte)algorithm << 9);
        return flags;
    }
}
```

### Task 1.4.2: Create Extended Block Header
**File**: `EmailDB.Format/Models/ExtendedBlockHeader.cs`
**Dependencies**: None
**Description**: Variable-length header for compressed/encrypted blocks

```csharp
namespace EmailDB.Format.Models;

public class ExtendedBlockHeader
{
    // Compression fields
    public long UncompressedSize { get; set; }
    
    // Encryption fields
    public byte[] IV { get; set; }
    public byte[] AuthTag { get; set; }
    public int KeyId { get; set; }
    
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Write header version
        writer.Write((byte)1);
        
        // Write uncompressed size if present
        if (UncompressedSize > 0)
        {
            writer.Write(true);
            writer.Write(UncompressedSize);
        }
        else
        {
            writer.Write(false);
        }
        
        // Write encryption data if present
        if (IV != null && IV.Length > 0)
        {
            writer.Write(true);
            writer.Write(IV.Length);
            writer.Write(IV);
            writer.Write(AuthTag.Length);
            writer.Write(AuthTag);
            writer.Write(KeyId);
        }
        else
        {
            writer.Write(false);
        }
        
        return ms.ToArray();
    }
    
    public static ExtendedBlockHeader Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        var header = new ExtendedBlockHeader();
        
        // Read version
        var version = reader.ReadByte();
        
        // Read uncompressed size
        if (reader.ReadBoolean())
        {
            header.UncompressedSize = reader.ReadInt64();
        }
        
        // Read encryption data
        if (reader.ReadBoolean())
        {
            var ivLength = reader.ReadInt32();
            header.IV = reader.ReadBytes(ivLength);
            var authTagLength = reader.ReadInt32();
            header.AuthTag = reader.ReadBytes(authTagLength);
            header.KeyId = reader.ReadInt32();
        }
        
        return header;
    }
}
```

### Task 1.4.3: Create Compression Infrastructure
**File**: `EmailDB.Format/Compression/ICompressionProvider.cs`
**Dependencies**: None
**Description**: Interface for compression providers

```csharp
namespace EmailDB.Format.Compression;

public interface ICompressionProvider
{
    CompressionAlgorithm AlgorithmId { get; }
    string Name { get; }
    Result<byte[]> Compress(byte[] data);
    Result<byte[]> Decompress(byte[] compressed, long uncompressedSize);
}
```

**File**: `EmailDB.Format/Compression/LZ4CompressionProvider.cs`
**Dependencies**: K4os.Compression.LZ4 NuGet package
**Description**: LZ4 compression implementation

```csharp
using K4os.Compression.LZ4;

namespace EmailDB.Format.Compression;

public class LZ4CompressionProvider : ICompressionProvider
{
    public CompressionAlgorithm AlgorithmId => CompressionAlgorithm.LZ4;
    public string Name => "LZ4";
    
    public Result<byte[]> Compress(byte[] data)
    {
        try
        {
            var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
            var compressed = new byte[maxCompressedSize];
            
            var compressedSize = LZ4Codec.Encode(
                data, 0, data.Length,
                compressed, 0, maxCompressedSize,
                LZ4Level.L00_FAST);
                
            if (compressedSize <= 0)
                return Result<byte[]>.Failure("LZ4 compression failed");
                
            Array.Resize(ref compressed, compressedSize);
            return Result<byte[]>.Success(compressed);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"LZ4 compression error: {ex.Message}");
        }
    }
    
    public Result<byte[]> Decompress(byte[] compressed, long uncompressedSize)
    {
        try
        {
            var decompressed = new byte[uncompressedSize];
            
            var decompressedSize = LZ4Codec.Decode(
                compressed, 0, compressed.Length,
                decompressed, 0, (int)uncompressedSize);
                
            if (decompressedSize != uncompressedSize)
                return Result<byte[]>.Failure("Decompressed size mismatch");
                
            return Result<byte[]>.Success(decompressed);
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"LZ4 decompression error: {ex.Message}");
        }
    }
}
```

### Task 1.4.4: Create Encryption Infrastructure
**File**: `EmailDB.Format/Encryption/IEncryptionProvider.cs`
**Dependencies**: None
**Description**: Interface for encryption providers

```csharp
namespace EmailDB.Format.Encryption;

public class EncryptedData
{
    public byte[] CipherText { get; set; }
    public byte[] IV { get; set; }
    public byte[] AuthTag { get; set; }
    public int KeyId { get; set; }
}

public interface IEncryptionProvider
{
    EncryptionAlgorithm AlgorithmId { get; }
    string Name { get; }
    int IVSize { get; }
    int AuthTagSize { get; }
    Result<EncryptedData> Encrypt(byte[] data, byte[] key, int keyId);
    Result<byte[]> Decrypt(EncryptedData encrypted, byte[] key);
}
```

### Task 1.4.5: Create Block Processor
**File**: `EmailDB.Format/FileManagement/BlockProcessor.cs`
**Dependencies**: AlgorithmRegistry, IKeyManager
**Description**: Handles compression/encryption pipeline

```csharp
namespace EmailDB.Format.FileManagement;

public class ProcessedBlock
{
    public byte[] Data { get; set; }
    public BlockFlags Flags { get; set; }
    public ExtendedBlockHeader ExtendedHeader { get; set; }
}

public class BlockProcessor
{
    private readonly AlgorithmRegistry _registry;
    private readonly IKeyManager _keyManager;
    
    public BlockProcessor(AlgorithmRegistry registry, IKeyManager keyManager)
    {
        _registry = registry;
        _keyManager = keyManager;
    }
    
    public async Task<Result<ProcessedBlock>> ProcessForWriteAsync(
        byte[] rawData,
        CompressionAlgorithm? compression,
        EncryptionAlgorithm? encryption,
        int? keyId)
    {
        var processedData = rawData;
        var flags = BlockFlags.None;
        var extendedHeader = new ExtendedBlockHeader();
        
        // Step 1: Compression
        if (compression.HasValue && compression.Value != CompressionAlgorithm.None)
        {
            var compressor = _registry.GetCompression(compression.Value);
            if (compressor == null)
                return Result<ProcessedBlock>.Failure($"Unknown compression algorithm: {compression}");
                
            var compressed = compressor.Compress(rawData);
            
            if (compressed.IsSuccess && compressed.Value.Length < rawData.Length)
            {
                processedData = compressed.Value;
                flags = flags.SetCompressionAlgorithm(compression.Value);
                extendedHeader.UncompressedSize = rawData.Length;
            }
        }
        
        // Step 2: Encryption
        if (encryption.HasValue && encryption.Value != EncryptionAlgorithm.None)
        {
            var encryptor = _registry.GetEncryption(encryption.Value);
            if (encryptor == null)
                return Result<ProcessedBlock>.Failure($"Unknown encryption algorithm: {encryption}");
                
            // Get encryption key
            var keyResult = await _keyManager.GetKeyAsync(keyId ?? 0);
            if (!keyResult.IsSuccess)
                return Result<ProcessedBlock>.Failure($"Failed to get encryption key: {keyResult.Error}");
                
            var encrypted = encryptor.Encrypt(processedData, keyResult.Value, keyId ?? 0);
            
            if (encrypted.IsSuccess)
            {
                processedData = encrypted.Value.CipherText;
                flags = flags.SetEncryptionAlgorithm(encryption.Value);
                extendedHeader.IV = encrypted.Value.IV;
                extendedHeader.AuthTag = encrypted.Value.AuthTag;
                extendedHeader.KeyId = encrypted.Value.KeyId;
            }
            else
            {
                return Result<ProcessedBlock>.Failure($"Encryption failed: {encrypted.Error}");
            }
        }
        
        return Result<ProcessedBlock>.Success(new ProcessedBlock
        {
            Data = processedData,
            Flags = flags,
            ExtendedHeader = extendedHeader
        });
    }
    
    public async Task<Result<byte[]>> ProcessForReadAsync(
        byte[] blockData,
        BlockFlags flags,
        ExtendedBlockHeader extendedHeader)
    {
        var processedData = blockData;
        
        // Step 1: Decryption
        var encryptionAlg = flags.GetEncryptionAlgorithm();
        if (encryptionAlg != EncryptionAlgorithm.None)
        {
            var decryptor = _registry.GetEncryption(encryptionAlg);
            if (decryptor == null)
                return Result<byte[]>.Failure($"Unknown encryption algorithm: {encryptionAlg}");
                
            var keyResult = await _keyManager.GetKeyAsync(extendedHeader.KeyId);
            if (!keyResult.IsSuccess)
                return Result<byte[]>.Failure($"Failed to get decryption key: {keyResult.Error}");
                
            var encrypted = new EncryptedData
            {
                CipherText = processedData,
                IV = extendedHeader.IV,
                AuthTag = extendedHeader.AuthTag,
                KeyId = extendedHeader.KeyId
            };
            
            var decrypted = decryptor.Decrypt(encrypted, keyResult.Value);
            if (!decrypted.IsSuccess)
                return Result<byte[]>.Failure($"Decryption failed: {decrypted.Error}");
                
            processedData = decrypted.Value;
        }
        
        // Step 2: Decompression
        var compressionAlg = flags.GetCompressionAlgorithm();
        if (compressionAlg != CompressionAlgorithm.None)
        {
            var decompressor = _registry.GetCompression(compressionAlg);
            if (decompressor == null)
                return Result<byte[]>.Failure($"Unknown compression algorithm: {compressionAlg}");
                
            var decompressed = decompressor.Decompress(
                processedData, 
                extendedHeader.UncompressedSize);
                
            if (!decompressed.IsSuccess)
                return Result<byte[]>.Failure($"Decompression failed: {decompressed.Error}");
                
            processedData = decompressed.Value;
        }
        
        return Result<byte[]>.Success(processedData);
    }
}
```

### Task 1.4.6: Update RawBlockManager
**File**: `EmailDB.Format/FileManagement/RawBlockManager.cs` (modifications)
**Dependencies**: BlockProcessor
**Description**: Integrate compression/encryption support

```csharp
// Add to RawBlockManager class:

private readonly BlockProcessor _processor;
private readonly CompressionConfig _compressionConfig;
private readonly EncryptionConfig _encryptionConfig;

public async Task<Result<long>> WriteBlockAsync(
    BlockType type,
    byte[] payload,
    CompressionAlgorithm? compression = null,
    EncryptionAlgorithm? encryption = null,
    int? keyId = null)
{
    try
    {
        // Use configuration if not specified
        compression ??= _compressionConfig?.GetCompressionForType(type);
        encryption ??= _encryptionConfig?.GetEncryptionForType(type);
        keyId ??= _encryptionConfig?.DefaultKeyId;
        
        // Process payload
        var processed = await _processor.ProcessForWriteAsync(
            payload, 
            compression, 
            encryption,
            keyId);
            
        if (!processed.IsSuccess)
            return Result<long>.Failure(processed.Error);
        
        // Prepare final payload
        byte[] finalPayload;
        if (processed.Value.ExtendedHeader != null)
        {
            var extHeader = processed.Value.ExtendedHeader.Serialize();
            finalPayload = new byte[extHeader.Length + processed.Value.Data.Length];
            Buffer.BlockCopy(extHeader, 0, finalPayload, 0, extHeader.Length);
            Buffer.BlockCopy(processed.Value.Data, 0, finalPayload, extHeader.Length, processed.Value.Data.Length);
        }
        else
        {
            finalPayload = processed.Value.Data;
        }
        
        // Create block with processed data
        var block = new Block
        {
            Type = type,
            Flags = (uint)processed.Value.Flags,
            Payload = finalPayload,
            PayloadLength = finalPayload.Length,
            PayloadEncoding = _defaultEncoding,
            Id = GetNextBlockId(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        
        // Continue with existing write logic...
        return await WriteBlockInternalAsync(block);
    }
    catch (Exception ex)
    {
        return Result<long>.Failure($"Failed to write block: {ex.Message}");
    }
}

// Update ReadBlockAsync to handle decompression/decryption
public async Task<Result<Block>> ReadBlockAsync(long offset)
{
    var blockResult = await ReadBlockInternalAsync(offset);
    if (!blockResult.IsSuccess)
        return blockResult;
        
    var block = blockResult.Value;
    var flags = (BlockFlags)block.Flags;
    
    // Check if block needs processing
    if ((flags & (BlockFlags.Compressed | BlockFlags.Encrypted)) != 0)
    {
        // Extract extended header
        var extHeader = ExtractExtendedHeader(block.Payload);
        if (extHeader == null)
            return Result<Block>.Failure("Failed to read extended header");
            
        // Get actual payload (after extended header)
        var payloadStart = GetExtendedHeaderSize(block.Payload);
        var actualPayload = new byte[block.Payload.Length - payloadStart];
        Buffer.BlockCopy(block.Payload, payloadStart, actualPayload, 0, actualPayload.Length);
        
        // Process payload
        var processed = await _processor.ProcessForReadAsync(
            actualPayload,
            flags,
            extHeader);
            
        if (!processed.IsSuccess)
            return Result<Block>.Failure($"Failed to process block: {processed.Error}");
            
        // Update block with processed data
        block.Payload = processed.Value;
        block.PayloadLength = processed.Value.Length;
    }
    
    return Result<Block>.Success(block);
}
```

## Section 1.5: In-Band Key Management System

### Task 1.5.1: Define Key Management Block Types
**File**: `EmailDB.Format/Models/BlockTypes/KeyManagerContent.cs`
**Dependencies**: BlockContent
**Description**: Block type for storing encrypted keys

```csharp
namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class KeyManagerContent : BlockContent
{
    [ProtoMember(1)]
    public int Version { get; set; }
    
    [ProtoMember(2)]
    public DateTime Created { get; set; }
    
    [ProtoMember(3)]
    public List<EncryptedKeyEntry> Keys { get; set; } = new();
    
    [ProtoMember(4)]
    public long? PreviousKeyManagerBlockId { get; set; }
    
    [ProtoMember(5)]
    public byte[] Salt { get; set; }
    
    public override BlockType GetBlockType() => BlockType.KeyManager;
}

[ProtoContract]
public class EncryptedKeyEntry
{
    [ProtoMember(1)]
    public int KeyId { get; set; }
    
    [ProtoMember(2)]
    public string Purpose { get; set; }
    
    [ProtoMember(3)]
    public EncryptionAlgorithm Algorithm { get; set; }
    
    [ProtoMember(4)]
    public byte[] EncryptedKey { get; set; }
    
    [ProtoMember(5)]
    public DateTime Created { get; set; }
    
    [ProtoMember(6)]
    public DateTime? Revoked { get; set; }
    
    [ProtoMember(7)]
    public Dictionary<string, string> Metadata { get; set; } = new();
}
```

### Task 1.5.2: Create Key Exchange Block Type
**File**: `EmailDB.Format/Models/BlockTypes/KeyExchangeContent.cs`
**Dependencies**: BlockContent
**Description**: Block type for key exchange methods

```csharp
namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class KeyExchangeContent : BlockContent
{
    [ProtoMember(1)]
    public int Version { get; set; }
    
    [ProtoMember(2)]
    public string Method { get; set; } // "password", "webauthn", "pgp", etc.
    
    [ProtoMember(3)]
    public byte[] EncryptedMasterKey { get; set; }
    
    [ProtoMember(4)]
    public byte[] MethodSpecificData { get; set; }
    
    [ProtoMember(5)]
    public DateTime Created { get; set; }
    
    [ProtoMember(6)]
    public bool IsActive { get; set; }
    
    public override BlockType GetBlockType() => BlockType.KeyExchange;
}
```

### Task 1.5.3: Create Key Manager Implementation
**File**: `EmailDB.Format/Security/BlockKeyManager.cs`
**Dependencies**: RawBlockManager, IKeyExchangeProvider
**Description**: Manages encryption keys stored in blocks

```csharp
namespace EmailDB.Format.Security;

public interface IKeyManager
{
    Task<Result> UnlockAsync(IKeyExchangeCredential credential);
    Task<Result<int>> CreateKeyAsync(string purpose, EncryptionAlgorithm algorithm);
    Task<Result<byte[]>> GetKeyAsync(int keyId);
    Task<Result> RotateKeyAsync(int oldKeyId, string reason);
    bool IsUnlocked { get; }
}

public class BlockKeyManager : IKeyManager
{
    private readonly RawBlockManager _blockManager;
    private readonly Dictionary<string, IKeyExchangeProvider> _keyExchangeProviders;
    private readonly IBlockContentSerializer _serializer;
    
    private byte[] _masterKey;
    private KeyManagerContent _currentKeyManager;
    private long _currentKeyManagerBlockId;
    private readonly Dictionary<int, byte[]> _keyCache = new();
    
    public bool IsUnlocked => _masterKey != null;
    
    public BlockKeyManager(
        RawBlockManager blockManager,
        IBlockContentSerializer serializer)
    {
        _blockManager = blockManager;
        _serializer = serializer;
        _keyExchangeProviders = new Dictionary<string, IKeyExchangeProvider>
        {
            { "password", new PasswordKeyExchangeProvider() },
            // Add other providers as implemented
        };
    }
    
    public async Task<Result> UnlockAsync(IKeyExchangeCredential credential)
    {
        // Find KeyExchange blocks
        var keyExchangeBlocks = await FindKeyExchangeBlocksAsync(credential.Method);
        
        foreach (var blockId in keyExchangeBlocks)
        {
            var blockResult = await _blockManager.ReadBlockAsync(blockId);
            if (!blockResult.IsSuccess) continue;
            
            var deserializeResult = _serializer.Deserialize<KeyExchangeContent>(
                blockResult.Value.Payload, 
                blockResult.Value.PayloadEncoding);
                
            if (!deserializeResult.IsSuccess) continue;
            
            var keyExchange = deserializeResult.Value;
            if (!keyExchange.IsActive) continue;
            
            var provider = _keyExchangeProviders[credential.Method];
            var masterKeyResult = await provider.UnlockMasterKeyAsync(
                keyExchange, 
                credential);
                
            if (masterKeyResult.IsSuccess)
            {
                _masterKey = masterKeyResult.Value;
                return await LoadCurrentKeyManagerAsync();
            }
        }
        
        return Result.Failure("Failed to unlock key manager");
    }
    
    public async Task<Result<int>> CreateKeyAsync(
        string purpose, 
        EncryptionAlgorithm algorithm)
    {
        if (!IsUnlocked)
            return Result<int>.Failure("Key manager is locked");
        
        // Generate new key
        var newKey = GenerateKey(algorithm);
        var keyId = GetNextKeyId();
        
        // Encrypt with master key
        var encryptedKey = await EncryptWithMasterKeyAsync(newKey);
        
        // Create new version of KeyManager block
        var newKeyManager = new KeyManagerContent
        {
            Version = _currentKeyManager.Version + 1,
            Created = DateTime.UtcNow,
            Keys = new List<EncryptedKeyEntry>(_currentKeyManager.Keys),
            PreviousKeyManagerBlockId = _currentKeyManagerBlockId,
            Salt = _currentKeyManager.Salt
        };
        
        newKeyManager.Keys.Add(new EncryptedKeyEntry
        {
            KeyId = keyId,
            Purpose = purpose,
            Algorithm = algorithm,
            EncryptedKey = encryptedKey,
            Created = DateTime.UtcNow
        });
        
        // Write new KeyManager block
        var result = await WriteKeyManagerBlockAsync(newKeyManager);
        if (result.IsSuccess)
        {
            _currentKeyManager = newKeyManager;
            _currentKeyManagerBlockId = result.Value;
            _keyCache[keyId] = newKey;
            
            return Result<int>.Success(keyId);
        }
        
        return Result<int>.Failure(result.Error);
    }
    
    public async Task<Result<byte[]>> GetKeyAsync(int keyId)
    {
        if (!IsUnlocked)
            return Result<byte[]>.Failure("Key manager is locked");
        
        // Check cache
        if (_keyCache.TryGetValue(keyId, out var cachedKey))
            return Result<byte[]>.Success(cachedKey);
        
        // Find in current key manager
        var entry = _currentKeyManager.Keys.FirstOrDefault(k => k.KeyId == keyId);
        if (entry == null)
            return Result<byte[]>.Failure($"Key {keyId} not found");
        
        if (entry.Revoked.HasValue)
            return Result<byte[]>.Failure($"Key {keyId} has been revoked");
        
        // Decrypt key
        var decryptedKey = await DecryptWithMasterKeyAsync(entry.EncryptedKey);
        _keyCache[keyId] = decryptedKey;
        
        return Result<byte[]>.Success(decryptedKey);
    }
    
    private async Task<Result<long>> WriteKeyManagerBlockAsync(
        KeyManagerContent content)
    {
        // Serialize content
        var serialized = _serializer.Serialize(content, PayloadEncoding.Protobuf);
        if (!serialized.IsSuccess)
            return Result<long>.Failure($"Failed to serialize key manager: {serialized.Error}");
        
        // Encrypt with master key
        var encrypted = await EncryptDataAsync(serialized.Value, _masterKey);
        
        // Write as encrypted block (no compression for security)
        return await _blockManager.WriteBlockAsync(
            BlockType.KeyManager,
            encrypted,
            compression: CompressionAlgorithm.None,
            encryption: EncryptionAlgorithm.AES256_GCM,
            keyId: 0); // Special marker for master key encryption
    }
    
    private byte[] GenerateKey(EncryptionAlgorithm algorithm)
    {
        return algorithm switch
        {
            EncryptionAlgorithm.AES256_GCM => GenerateRandomBytes(32),
            EncryptionAlgorithm.ChaCha20_Poly1305 => GenerateRandomBytes(32),
            _ => throw new NotSupportedException($"Unsupported algorithm: {algorithm}")
        };
    }
    
    private byte[] GenerateRandomBytes(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return bytes;
    }
}
```

## Section 1.6: Configuration System

### Task 1.6.1: Create Compression Configuration
**File**: `EmailDB.Format/Configuration/CompressionConfig.cs`
**Dependencies**: None
**Description**: Configuration for compression behavior

```csharp
namespace EmailDB.Format.Configuration;

public class CompressionConfig
{
    public bool EnableCompression { get; set; } = true;
    public CompressionAlgorithm DefaultAlgorithm { get; set; } = CompressionAlgorithm.LZ4;
    public int MinSizeForCompression { get; set; } = 1024; // 1KB minimum
    public Dictionary<BlockType, CompressionAlgorithm> BlockTypeOverrides { get; set; } = new();
    
    public CompressionAlgorithm GetCompressionForType(BlockType type)
    {
        if (!EnableCompression)
            return CompressionAlgorithm.None;
            
        return BlockTypeOverrides.TryGetValue(type, out var algorithm) 
            ? algorithm 
            : DefaultAlgorithm;
    }
    
    public bool ShouldCompress(BlockType type, int size)
    {
        return EnableCompression && size >= MinSizeForCompression;
    }
}
```

### Task 1.6.2: Create Encryption Configuration
**File**: `EmailDB.Format/Configuration/EncryptionConfig.cs`
**Dependencies**: None
**Description**: Configuration for encryption behavior

```csharp
namespace EmailDB.Format.Configuration;

public class EncryptionConfig
{
    public bool EnableEncryption { get; set; } = false;
    public EncryptionAlgorithm DefaultAlgorithm { get; set; } = EncryptionAlgorithm.AES256_GCM;
    public HashSet<BlockType> EncryptedBlockTypes { get; set; } = new()
    {
        BlockType.Segment,      // Email content
        BlockType.EmailBatch,   // Batched emails
        BlockType.Folder,       // Folder metadata
        BlockType.KeyManager    // Always encrypted
    };
    public int DefaultKeyId { get; set; } = 1;
    
    public EncryptionAlgorithm GetEncryptionForType(BlockType type)
    {
        if (!EnableEncryption && type != BlockType.KeyManager)
            return EncryptionAlgorithm.None;
            
        return EncryptedBlockTypes.Contains(type) ? DefaultAlgorithm : EncryptionAlgorithm.None;
    }
    
    public bool ShouldEncrypt(BlockType type)
    {
        return type == BlockType.KeyManager || (EnableEncryption && EncryptedBlockTypes.Contains(type));
    }
}
```

## Implementation Timeline

### Week 1: Foundation (Days 1-5)
**Day 1-2: Block Types and Models**
- [ ] Task 1.1.1: Create EmailEnvelope model
- [ ] Task 1.1.2: Create FolderEnvelopeBlock
- [ ] Task 1.1.3: Update existing block types
- [ ] Task 1.1.4: Create ZoneTree block types

**Day 3-4: Email Storage Infrastructure**
- [ ] Task 1.2.1: Implement EmailHashedID system
- [ ] Task 1.2.2: Create AdaptiveBlockSizer
- [ ] Task 1.2.3: Implement EmailBlockBuilder
- [ ] Task 1.2.4: Create EmailStorageManager (basic version)

**Day 5: Serialization Updates**
- [ ] Task 1.3.1: Update DefaultBlockContentSerializer
- [ ] Ensure all new block types have Protobuf definitions
- [ ] Test serialization round-trips

### Week 2: Security Infrastructure (Days 6-10)
**Day 6-7: Compression System**
- [ ] Task 1.4.1: Define BlockFlags
- [ ] Task 1.4.2: Create ExtendedBlockHeader
- [ ] Task 1.4.3: Create compression infrastructure
- [ ] Implement LZ4, Gzip, Zstd providers

**Day 8-9: Encryption System**
- [ ] Task 1.4.4: Create encryption infrastructure
- [ ] Implement AES-GCM provider
- [ ] Task 1.4.5: Create BlockProcessor
- [ ] Task 1.4.6: Begin RawBlockManager updates

**Day 10: Key Management Foundation**
- [ ] Task 1.5.1: Define KeyManagerContent block
- [ ] Task 1.5.2: Create KeyExchangeContent block
- [ ] Task 1.5.3: Begin BlockKeyManager implementation

### Week 3: Integration and Testing (Days 11-15)
**Day 11-12: Complete Integration**
- [ ] Complete RawBlockManager updates
- [ ] Finish BlockKeyManager implementation
- [ ] Task 1.6.1: Create CompressionConfig
- [ ] Task 1.6.2: Create EncryptionConfig

**Day 13-14: Testing**
- [ ] Unit tests for each component
- [ ] Integration tests for block processing
- [ ] Performance benchmarks
- [ ] Compression ratio tests

**Day 15: Documentation and Cleanup**
- [ ] Code documentation
- [ ] Update architecture diagrams
- [ ] Performance tuning
- [ ] Code review preparation

## Dependencies and Prerequisites

### NuGet Packages Required
- K4os.Compression.LZ4
- ZstdNet (for Zstd compression)
- Protobuf-net (already in use)
- System.Security.Cryptography (built-in)

### Existing Code Dependencies
- RawBlockManager (minimal modifications)
- IBlockContent interface
- IPayloadEncoding interface
- Result<T> pattern
- Block model
- ZoneTree infrastructure

## Success Criteria

1. **All new block types properly serialized** with Protobuf
2. **Email batching working** with adaptive sizing
3. **Compression achieving** >50% reduction on typical emails
4. **Encryption/decryption** transparent to higher layers
5. **Key management** fully self-contained in blocks
6. **All tests passing** with >90% code coverage
7. **Performance targets met**:
   - Block write: >50MB/s
   - Block read: >100MB/s
   - Compression overhead: <10ms per MB
   - Encryption overhead: <5ms per MB

## Risk Mitigation

1. **Performance Risk**: Profile early and often
2. **Compatibility Risk**: Extensive testing with existing blocks
3. **Security Risk**: Security review of encryption implementation
4. **Integration Risk**: Incremental integration with existing code
5. **Complexity Risk**: Clear interfaces and separation of concerns