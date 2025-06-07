using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.Format.Models;
using EmailDB.Format.Helpers;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Append-only block storage that packs multiple emails into blocks.
/// Each email gets a unique ID composed of (BlockId, LocalId).
/// </summary>
public class AppendOnlyBlockStore : IDisposable
{
    private readonly string _filePath;
    private readonly int _blockSizeThreshold;
    private readonly AsyncReaderWriterLock _lock = new();
    private readonly FileStream _fileStream;
    private readonly BinaryWriter _writer;
    private readonly Dictionary<long, BlockIndex> _blockIndex = new();
    private readonly Dictionary<(long blockId, int localId), long> _emailIndex = new();
    
    private BlockBuilder _currentBlock;
    private long _nextBlockId = 1;
    private bool _disposed;

    public AppendOnlyBlockStore(string filePath, int blockSizeThreshold = 1024 * 1024) // 1MB default
    {
        _filePath = filePath;
        _blockSizeThreshold = blockSizeThreshold;
        
        // Open file in append mode
        _fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _writer = new BinaryWriter(_fileStream);
        
        // Load existing index if file exists
        if (_fileStream.Length > 0)
        {
            LoadIndex();
        }
        
        // Start new block
        _currentBlock = new BlockBuilder(_nextBlockId++);
    }

    /// <summary>
    /// Appends an email and returns its unique ID (BlockId, LocalId).
    /// </summary>
    public async Task<(long blockId, int localId)> AppendEmailAsync(byte[] emailData)
    {
        await _lock.AcquireWriterLock();
        try
        {
            // Try to add to current block
            if (!_currentBlock.TryAddEmail(emailData, _blockSizeThreshold))
            {
                // Current block is full, flush it
                await FlushCurrentBlockAsync();
                
                // Start new block
                _currentBlock = new BlockBuilder(_nextBlockId++);
                
                // Add to new block (should always succeed)
                if (!_currentBlock.TryAddEmail(emailData, _blockSizeThreshold))
                {
                    throw new InvalidOperationException($"Email size {emailData.Length} exceeds block threshold {_blockSizeThreshold}");
                }
            }
            
            return (_currentBlock.BlockId, _currentBlock.EmailCount - 1);
        }
        finally
        {
            _lock.ReleaseWriterLock();
        }
    }

    /// <summary>
    /// Reads an email by its composite ID.
    /// </summary>
    public async Task<byte[]> ReadEmailAsync(long blockId, int localId)
    {
        await _lock.AcquireReaderLock();
        try
        {
            // Check if it's in the current block (not yet flushed)
            if (blockId == _currentBlock.BlockId)
            {
                return _currentBlock.GetEmail(localId);
            }
            
            // Look up in index
            if (!_blockIndex.TryGetValue(blockId, out var blockIndex))
            {
                throw new ArgumentException($"Block {blockId} not found");
            }
            
            // Read the block data (skip header, read only data portion)
            var blockData = new byte[blockIndex.Size];
            _fileStream.Seek(blockIndex.Offset + 28, SeekOrigin.Begin); // Skip header
            await _fileStream.ReadAsync(blockData, 0, blockData.Length);
            
            // Parse block and extract email
            var block = BlockParser.Parse(blockData);
            if (localId >= block.EmailCount)
            {
                throw new ArgumentException($"Email {localId} not found in block {blockId}");
            }
            
            return block.GetEmail(localId);
        }
        finally
        {
            _lock.ReleaseReaderLock();
        }
    }

    /// <summary>
    /// Flushes the current block to disk.
    /// </summary>
    public async Task FlushAsync()
    {
        await _lock.AcquireWriterLock();
        try
        {
            if (_currentBlock.EmailCount > 0)
            {
                await FlushCurrentBlockAsync();
                _currentBlock = new BlockBuilder(_nextBlockId++);
            }
        }
        finally
        {
            _lock.ReleaseWriterLock();
        }
    }

    private async Task FlushCurrentBlockAsync()
    {
        if (_currentBlock.EmailCount == 0) return;
        
        var blockData = _currentBlock.Build();
        var offset = _fileStream.Position;
        
        // Write block header
        _writer.Write(BlockMarker.Start); // 4 bytes
        _writer.Write(_currentBlock.BlockId); // 8 bytes
        _writer.Write(blockData.Length); // 4 bytes
        _writer.Write(_currentBlock.EmailCount); // 4 bytes
        _writer.Write(DateTime.UtcNow.Ticks); // 8 bytes
        
        // Write block data
        await _fileStream.WriteAsync(blockData, 0, blockData.Length);
        
        // Write block trailer
        _writer.Write(BlockMarker.End); // 4 bytes
        
        await _fileStream.FlushAsync();
        
        // Update index
        _blockIndex[_currentBlock.BlockId] = new BlockIndex
        {
            BlockId = _currentBlock.BlockId,
            Offset = offset,
            Size = blockData.Length, // just the data size
            EmailCount = _currentBlock.EmailCount
        };
        
        // Update email index
        for (int i = 0; i < _currentBlock.EmailCount; i++)
        {
            _emailIndex[(_currentBlock.BlockId, i)] = offset;
        }
    }

    private void LoadIndex()
    {
        _fileStream.Seek(0, SeekOrigin.Begin);
        var reader = new BinaryReader(_fileStream);
        
        while (_fileStream.Position < _fileStream.Length)
        {
            var marker = reader.ReadInt32();
            if (marker != BlockMarker.Start)
            {
                throw new InvalidDataException($"Invalid block marker at position {_fileStream.Position - 4}");
            }
            
            var blockId = reader.ReadInt64();
            var blockSize = reader.ReadInt32();
            var emailCount = reader.ReadInt32();
            var timestamp = reader.ReadInt64();
            
            var blockIndex = new BlockIndex
            {
                BlockId = blockId,
                Offset = _fileStream.Position - 28, // Back to start of block
                Size = blockSize + 32,
                EmailCount = emailCount
            };
            
            _blockIndex[blockId] = blockIndex;
            
            // Update email index
            for (int i = 0; i < emailCount; i++)
            {
                _emailIndex[(blockId, i)] = blockIndex.Offset;
            }
            
            // Skip to next block
            _fileStream.Seek(blockSize + 4, SeekOrigin.Current); // data + end marker
            
            _nextBlockId = Math.Max(_nextBlockId, blockId + 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        // Flush any pending data
        FlushAsync().GetAwaiter().GetResult();
        
        _writer?.Dispose();
        _fileStream?.Dispose();
        _disposed = true;
    }

    private class BlockBuilder
    {
        private readonly List<byte[]> _emails = new();
        private int _currentSize = 0;
        
        public long BlockId { get; }
        public int EmailCount => _emails.Count;
        
        public BlockBuilder(long blockId)
        {
            BlockId = blockId;
        }
        
        public bool TryAddEmail(byte[] emailData, int sizeThreshold)
        {
            var emailSize = 4 + emailData.Length; // length prefix + data
            
            // Allow at least one email per block
            if (_emails.Count == 0 || _currentSize + emailSize <= sizeThreshold)
            {
                _emails.Add(emailData);
                _currentSize += emailSize;
                return true;
            }
            
            return false;
        }
        
        public byte[] GetEmail(int localId)
        {
            if (localId >= _emails.Count)
                throw new ArgumentOutOfRangeException(nameof(localId));
                
            return _emails[localId];
        }
        
        public byte[] Build()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            foreach (var email in _emails)
            {
                writer.Write(email.Length);
                writer.Write(email);
            }
            
            return ms.ToArray();
        }
    }

    private static class BlockParser
    {
        public static ParsedBlock Parse(byte[] blockData)
        {
            var emails = new List<byte[]>();
            using var ms = new MemoryStream(blockData);
            using var reader = new BinaryReader(ms);
            
            while (ms.Position < ms.Length)
            {
                var length = reader.ReadInt32();
                var data = reader.ReadBytes(length);
                emails.Add(data);
            }
            
            return new ParsedBlock(emails);
        }
    }

    private class ParsedBlock
    {
        private readonly List<byte[]> _emails;
        
        public int EmailCount => _emails.Count;
        
        public ParsedBlock(List<byte[]> emails)
        {
            _emails = emails;
        }
        
        public byte[] GetEmail(int localId)
        {
            if (localId >= _emails.Count)
                throw new ArgumentOutOfRangeException(nameof(localId));
                
            return _emails[localId];
        }
    }

    private class BlockIndex
    {
        public long BlockId { get; set; }
        public long Offset { get; set; }
        public int Size { get; set; }
        public int EmailCount { get; set; }
    }

    private static class BlockMarker
    {
        public const int Start = 0x424C4F43; // "BLOC"
        public const int End = 0x454E4442;   // "ENDB"
    }
}

/// <summary>
/// Represents a unique email identifier in the append-only store.
/// </summary>
public struct EmailId : IEquatable<EmailId>
{
    public long BlockId { get; }
    public int LocalId { get; }
    
    public EmailId(long blockId, int localId)
    {
        BlockId = blockId;
        LocalId = localId;
    }
    
    public override string ToString() => $"{BlockId}:{LocalId}";
    
    public static EmailId Parse(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 2)
            throw new FormatException("Invalid EmailId format");
            
        return new EmailId(long.Parse(parts[0]), int.Parse(parts[1]));
    }
    
    public bool Equals(EmailId other) => BlockId == other.BlockId && LocalId == other.LocalId;
    public override bool Equals(object obj) => obj is EmailId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(BlockId, LocalId);
    public static bool operator ==(EmailId left, EmailId right) => left.Equals(right);
    public static bool operator !=(EmailId left, EmailId right) => !left.Equals(right);
}