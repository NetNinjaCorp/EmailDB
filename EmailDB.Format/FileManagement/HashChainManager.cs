using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages cryptographic hash chains for EmailDB blocks, providing
/// tamper-evident storage and immutable audit trails for archiving.
/// </summary>
public class HashChainManager
{
    private readonly RawBlockManager _blockManager;
    private readonly Dictionary<long, HashChainEntry> _hashChainIndex;
    private readonly object _lock = new object();
    
    // Special block ID range for hash chain blocks
    private const long HASH_CHAIN_ID_BASE = 2_000_000_000_000;
    private long _nextHashChainId = HASH_CHAIN_ID_BASE;
    
    // Genesis block for the chain
    private const string GENESIS_HASH = "0000000000000000000000000000000000000000000000000000000000000000";
    private HashChainEntry _latestEntry;
    
    public HashChainManager(RawBlockManager blockManager)
    {
        _blockManager = blockManager;
        _hashChainIndex = new Dictionary<long, HashChainEntry>();
        
        // Initialize the chain
        Task.Run(async () => await InitializeChainAsync());
    }

    /// <summary>
    /// Adds a block to the hash chain, creating an immutable record.
    /// </summary>
    public async Task<Result<HashChainEntry>> AddToChainAsync(Block block)
    {
        try
        {
            // Calculate block hash
            var blockHash = CalculateBlockHash(block);
            
            // Get previous hash
            string previousHash;
            long sequenceNumber;
            
            lock (_lock)
            {
                if (_latestEntry == null)
                {
                    previousHash = GENESIS_HASH;
                    sequenceNumber = 0;
                }
                else
                {
                    previousHash = _latestEntry.ChainHash;
                    sequenceNumber = _latestEntry.SequenceNumber + 1;
                }
            }
            
            // Calculate chain hash (combines current block hash with previous chain hash)
            var chainHash = CalculateChainHash(blockHash, previousHash);
            
            // Create hash chain entry
            var entry = new HashChainEntry
            {
                BlockId = block.BlockId,
                SequenceNumber = sequenceNumber,
                Timestamp = DateTime.UtcNow,
                BlockHash = blockHash,
                PreviousHash = previousHash,
                ChainHash = chainHash,
                BlockType = block.Type,
                BlockSize = block.Payload?.Length ?? 0
            };
            
            // Persist hash chain entry
            var persistResult = await PersistHashChainEntryAsync(entry);
            if (!persistResult.IsSuccess)
            {
                return Result<HashChainEntry>.Failure($"Failed to persist hash chain entry: {persistResult.Error}");
            }
            
            // Update index
            lock (_lock)
            {
                _hashChainIndex[block.BlockId] = entry;
                _latestEntry = entry;
            }
            
            return Result<HashChainEntry>.Success(entry);
        }
        catch (Exception ex)
        {
            return Result<HashChainEntry>.Failure($"Failed to add block to hash chain: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies the integrity of a specific block in the chain.
    /// </summary>
    public async Task<Result<HashChainVerification>> VerifyBlockAsync(long blockId)
    {
        // Get the hash chain entry
        HashChainEntry entry;
        lock (_lock)
        {
            if (!_hashChainIndex.TryGetValue(blockId, out entry))
            {
                return Result<HashChainVerification>.Failure($"Block {blockId} not found in hash chain");
            }
        }
        
        // Read the actual block
        var blockResult = await _blockManager.ReadBlockAsync(blockId);
        if (!blockResult.IsSuccess || blockResult.Value == null)
        {
            return Result<HashChainVerification>.Success(new HashChainVerification
            {
                BlockId = blockId,
                IsValid = false,
                Error = "Block not found or corrupted"
            });
        }
        
        // Verify block hash
        var actualHash = CalculateBlockHash(blockResult.Value);
        var hashMatches = actualHash == entry.BlockHash;
        
        // Verify chain integrity
        var chainHash = CalculateChainHash(entry.BlockHash, entry.PreviousHash);
        var chainValid = chainHash == entry.ChainHash;
        
        return Result<HashChainVerification>.Success(new HashChainVerification
        {
            BlockId = blockId,
            IsValid = hashMatches && chainValid,
            ActualBlockHash = actualHash,
            ExpectedBlockHash = entry.BlockHash,
            ChainValid = chainValid,
            Error = !hashMatches ? "Block hash mismatch" : (!chainValid ? "Chain integrity broken" : null)
        });
    }

    /// <summary>
    /// Verifies the entire hash chain from genesis to the latest block.
    /// </summary>
    public async Task<Result<ChainVerificationResult>> VerifyEntireChainAsync()
    {
        var result = new ChainVerificationResult
        {
            StartTime = DateTime.UtcNow,
            TotalBlocks = 0,
            ValidBlocks = 0,
            InvalidBlocks = new List<long>(),
            BrokenChainPoints = new List<long>()
        };
        
        // Get all hash chain entries ordered by sequence
        var entries = _hashChainIndex.Values.OrderBy(e => e.SequenceNumber).ToList();
        result.TotalBlocks = entries.Count;
        
        string expectedPreviousHash = GENESIS_HASH;
        
        foreach (var entry in entries)
        {
            // Verify previous hash linkage
            if (entry.PreviousHash != expectedPreviousHash)
            {
                result.BrokenChainPoints.Add(entry.BlockId);
                result.ChainIntegrity = false;
            }
            
            // Verify block
            var verification = await VerifyBlockAsync(entry.BlockId);
            if (verification.IsSuccess && verification.Value.IsValid)
            {
                result.ValidBlocks++;
            }
            else
            {
                result.InvalidBlocks.Add(entry.BlockId);
            }
            
            expectedPreviousHash = entry.ChainHash;
        }
        
        result.EndTime = DateTime.UtcNow;
        result.VerificationTime = result.EndTime - result.StartTime;
        result.ChainIntegrity = result.ChainIntegrity ?? (result.BrokenChainPoints.Count == 0);
        
        return Result<ChainVerificationResult>.Success(result);
    }

    /// <summary>
    /// Gets the hash chain entry for a specific block.
    /// </summary>
    public HashChainEntry GetChainEntry(long blockId)
    {
        lock (_lock)
        {
            return _hashChainIndex.TryGetValue(blockId, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Gets the current chain head (latest entry).
    /// </summary>
    public HashChainEntry GetChainHead()
    {
        lock (_lock)
        {
            return _latestEntry;
        }
    }

    /// <summary>
    /// Exports the hash chain for archival or verification purposes.
    /// </summary>
    public async Task<Result<HashChainExport>> ExportChainAsync(long? fromSequence = null, long? toSequence = null)
    {
        var entries = _hashChainIndex.Values.OrderBy(e => e.SequenceNumber);
        
        if (fromSequence.HasValue)
            entries = entries.Where(e => e.SequenceNumber >= fromSequence.Value).OrderBy(e => e.SequenceNumber);
        
        if (toSequence.HasValue)
            entries = entries.Where(e => e.SequenceNumber <= toSequence.Value).OrderBy(e => e.SequenceNumber);
        
        var export = new HashChainExport
        {
            ExportTime = DateTime.UtcNow,
            GenesisHash = GENESIS_HASH,
            Entries = entries.ToList(),
            ChainHead = _latestEntry
        };
        
        // Calculate merkle root for the export
        export.MerkleRoot = CalculateMerkleRoot(export.Entries.Select(e => e.ChainHash).ToList());
        
        return Result<HashChainExport>.Success(export);
    }

    private string CalculateBlockHash(Block block)
    {
        using var sha256 = SHA256.Create();
        var data = new List<byte>();
        
        // Include all immutable block properties in hash
        data.AddRange(BitConverter.GetBytes(block.Version));
        data.Add((byte)block.Type);
        data.Add(block.Flags);
        data.Add((byte)block.Encoding);
        data.AddRange(BitConverter.GetBytes(block.Timestamp));
        data.AddRange(BitConverter.GetBytes(block.BlockId));
        
        if (block.Payload != null)
        {
            data.AddRange(block.Payload);
        }
        
        var hash = sha256.ComputeHash(data.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string CalculateChainHash(string blockHash, string previousHash)
    {
        using var sha256 = SHA256.Create();
        var combined = $"{previousHash}{blockHash}";
        var bytes = Encoding.UTF8.GetBytes(combined);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string CalculateMerkleRoot(List<string> hashes)
    {
        if (hashes.Count == 0) return GENESIS_HASH;
        if (hashes.Count == 1) return hashes[0];
        
        var level = new List<string>(hashes);
        
        while (level.Count > 1)
        {
            var nextLevel = new List<string>();
            
            for (int i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = (i + 1 < level.Count) ? level[i + 1] : left;
                
                using var sha256 = SHA256.Create();
                var combined = left + right;
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                nextLevel.Add(Convert.ToHexString(hash).ToLowerInvariant());
            }
            
            level = nextLevel;
        }
        
        return level[0];
    }

    private async Task InitializeChainAsync()
    {
        // Load existing hash chain from special blocks
        var locations = _blockManager.GetBlockLocations();
        var hashChainBlocks = locations.Where(l => l.Key >= HASH_CHAIN_ID_BASE).OrderBy(l => l.Key);
        
        foreach (var (blockId, _) in hashChainBlocks)
        {
            var result = await _blockManager.ReadBlockAsync(blockId);
            if (result.IsSuccess && result.Value != null)
            {
                var entry = DeserializeHashChainEntry(result.Value.Payload);
                if (entry != null)
                {
                    lock (_lock)
                    {
                        _hashChainIndex[entry.BlockId] = entry;
                        if (_latestEntry == null || entry.SequenceNumber > _latestEntry.SequenceNumber)
                        {
                            _latestEntry = entry;
                        }
                    }
                }
            }
        }
        
        // Update next ID
        if (_latestEntry != null)
        {
            _nextHashChainId = Math.Max(_nextHashChainId, HASH_CHAIN_ID_BASE + _latestEntry.SequenceNumber + 1);
        }
    }

    private async Task<Result> PersistHashChainEntryAsync(HashChainEntry entry)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var block = new Block
        {
            Version = 1,
            Type = BlockType.Metadata,
            Flags = 0x40, // Hash chain flag
            Encoding = PayloadEncoding.Json,
            Timestamp = DateTime.UtcNow.Ticks,
            BlockId = _nextHashChainId++,
            Payload = Encoding.UTF8.GetBytes(json)
        };
        
        var writeResult = await _blockManager.WriteBlockAsync(block);
        return writeResult.IsSuccess ? Result.Success() : Result.Failure(writeResult.Error);
    }

    private HashChainEntry DeserializeHashChainEntry(byte[] payload)
    {
        try
        {
            var json = Encoding.UTF8.GetString(payload);
            return System.Text.Json.JsonSerializer.Deserialize<HashChainEntry>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Represents an entry in the hash chain.
/// </summary>
public class HashChainEntry
{
    public long BlockId { get; set; }
    public long SequenceNumber { get; set; }
    public DateTime Timestamp { get; set; }
    public string BlockHash { get; set; }
    public string PreviousHash { get; set; }
    public string ChainHash { get; set; }
    public BlockType BlockType { get; set; }
    public int BlockSize { get; set; }
}

/// <summary>
/// Result of verifying a block in the hash chain.
/// </summary>
public class HashChainVerification
{
    public long BlockId { get; set; }
    public bool IsValid { get; set; }
    public string ActualBlockHash { get; set; }
    public string ExpectedBlockHash { get; set; }
    public bool ChainValid { get; set; }
    public string Error { get; set; }
}

/// <summary>
/// Result of verifying the entire hash chain.
/// </summary>
public class ChainVerificationResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan VerificationTime { get; set; }
    public int TotalBlocks { get; set; }
    public int ValidBlocks { get; set; }
    public List<long> InvalidBlocks { get; set; } = new();
    public List<long> BrokenChainPoints { get; set; } = new();
    public bool? ChainIntegrity { get; set; }
}

/// <summary>
/// Export of the hash chain for archival purposes.
/// </summary>
public class HashChainExport
{
    public DateTime ExportTime { get; set; }
    public string GenesisHash { get; set; }
    public List<HashChainEntry> Entries { get; set; }
    public HashChainEntry ChainHead { get; set; }
    public string MerkleRoot { get; set; }
}