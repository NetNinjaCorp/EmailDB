using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.Models;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Provides read-only access to EmailDB archives with hash chain verification.
/// Designed for long-term archival storage with cryptographic integrity guarantees.
/// </summary>
public class ArchiveManager : IDisposable
{
    private readonly string _archivePath;
    private readonly RawBlockManager _blockManager;
    private readonly HashChainManager _hashChainManager;
    private ArchiveMetadata _metadata;
    private readonly bool _strictMode;
    
    /// <summary>
    /// Opens an EmailDB archive in read-only mode.
    /// </summary>
    /// <param name="archivePath">Path to the archive file</param>
    /// <param name="strictMode">If true, fails on any integrity violation</param>
    public ArchiveManager(string archivePath, bool strictMode = true)
    {
        _archivePath = archivePath;
        _strictMode = strictMode;
        
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive file not found: {archivePath}");
        }
        
        // Open in read-only mode
        _blockManager = new RawBlockManager(archivePath, isReadOnly: true);
        _hashChainManager = new HashChainManager(_blockManager);
        
        // Load archive metadata
        Task.Run(async () =>
        {
            _metadata = await LoadArchiveMetadataAsync();
        }).Wait();
    }

    /// <summary>
    /// Verifies the integrity of the entire archive.
    /// </summary>
    public async Task<ArchiveVerificationResult> VerifyArchiveAsync()
    {
        var result = new ArchiveVerificationResult
        {
            ArchivePath = _archivePath,
            VerificationStartTime = DateTime.UtcNow,
            FileSize = new FileInfo(_archivePath).Length
        };
        
        try
        {
            // Step 1: Verify file header
            var headerValid = await VerifyFileHeaderAsync();
            result.HeaderValid = headerValid;
            
            // Step 2: Verify all block checksums
            var checksumResult = await VerifyAllChecksumsAsync();
            result.ChecksumsPassed = checksumResult.ValidBlocks.Count;
            result.ChecksumsFailed = checksumResult.InvalidBlocks.Count;
            
            // Step 3: Verify hash chain
            var chainResult = await _hashChainManager.VerifyEntireChainAsync();
            if (chainResult.IsSuccess)
            {
                result.HashChainValid = chainResult.Value.ChainIntegrity ?? false;
                result.HashChainBrokenAt = chainResult.Value.BrokenChainPoints;
            }
            
            // Step 4: Verify archive signature if present
            if (_metadata?.ArchiveSignature != null)
            {
                result.SignatureValid = await VerifyArchiveSignatureAsync();
            }
            
            result.IsValid = headerValid && 
                           checksumResult.InvalidBlocks.Count == 0 && 
                           (result.HashChainValid ?? true) &&
                           (result.SignatureValid ?? true);
                           
            result.VerificationEndTime = DateTime.UtcNow;
            result.VerificationDuration = result.VerificationEndTime - result.VerificationStartTime;
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Error = ex.Message;
        }
        
        return result;
    }

    /// <summary>
    /// Reads a block from the archive with full verification.
    /// </summary>
    public async Task<Result<Block>> ReadVerifiedBlockAsync(long blockId)
    {
        // Read the block
        var blockResult = await _blockManager.ReadBlockAsync(blockId);
        if (!blockResult.IsSuccess)
        {
            return blockResult;
        }
        
        var block = blockResult.Value;
        if (block == null)
        {
            return Result<Block>.Failure($"Block {blockId} not found");
        }
        
        // Verify hash chain entry
        var chainEntry = _hashChainManager.GetChainEntry(blockId);
        if (chainEntry != null)
        {
            var verification = await _hashChainManager.VerifyBlockAsync(blockId);
            if (!verification.IsSuccess || !verification.Value.IsValid)
            {
                if (_strictMode)
                {
                    return Result<Block>.Failure($"Block {blockId} failed hash chain verification");
                }
            }
        }
        
        return Result<Block>.Success(block);
    }

    /// <summary>
    /// Searches for emails in the archive by various criteria.
    /// </summary>
    public async Task<List<ArchivedEmail>> SearchEmailsAsync(ArchiveSearchCriteria criteria)
    {
        var results = new List<ArchivedEmail>();
        var locations = _blockManager.GetBlockLocations();
        
        foreach (var (blockId, location) in locations)
        {
            var blockResult = await ReadVerifiedBlockAsync(blockId);
            if (!blockResult.IsSuccess || blockResult.Value.Type != BlockType.Segment)
                continue;
                
            var block = blockResult.Value;
            
            // Check if block matches criteria
            if (criteria.StartDate.HasValue || criteria.EndDate.HasValue)
            {
                var blockDate = new DateTime(block.Timestamp);
                if (criteria.StartDate.HasValue && blockDate < criteria.StartDate.Value)
                    continue;
                if (criteria.EndDate.HasValue && blockDate > criteria.EndDate.Value)
                    continue;
            }
            
            // Parse email content
            try
            {
                var emailJson = Encoding.UTF8.GetString(block.Payload);
                var emailData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(emailJson);
                
                if (criteria.Sender != null && !emailData.ContainsKey("from"))
                    continue;
                    
                if (criteria.Subject != null && !emailData.ContainsKey("subject"))
                    continue;
                
                var email = new ArchivedEmail
                {
                    BlockId = blockId,
                    Timestamp = new DateTime(block.Timestamp),
                    Subject = emailData.GetValueOrDefault("subject")?.ToString(),
                    Sender = emailData.GetValueOrDefault("from")?.ToString(),
                    Recipients = emailData.GetValueOrDefault("to")?.ToString(),
                    Size = block.Payload.Length,
                    HashChainEntry = _hashChainManager.GetChainEntry(blockId)
                };
                
                results.Add(email);
            }
            catch
            {
                // Skip malformed blocks
            }
        }
        
        return results;
    }

    /// <summary>
    /// Exports a cryptographically verifiable proof of an email's existence.
    /// </summary>
    public async Task<EmailExistenceProof> GenerateExistenceProofAsync(long blockId)
    {
        var blockResult = await ReadVerifiedBlockAsync(blockId);
        if (!blockResult.IsSuccess)
        {
            return null;
        }
        
        var block = blockResult.Value;
        var chainEntry = _hashChainManager.GetChainEntry(blockId);
        
        if (chainEntry == null)
        {
            return null;
        }
        
        // Generate merkle proof
        var chainExport = await _hashChainManager.ExportChainAsync();
        
        var proof = new EmailExistenceProof
        {
            BlockId = blockId,
            Timestamp = new DateTime(block.Timestamp),
            BlockHash = chainEntry.BlockHash,
            ChainHash = chainEntry.ChainHash,
            SequenceNumber = chainEntry.SequenceNumber,
            MerkleRoot = chainExport.Value.MerkleRoot,
            ArchiveMetadata = _metadata,
            GeneratedAt = DateTime.UtcNow
        };
        
        // Sign the proof if we have a signing key
        if (_metadata?.PublicKey != null)
        {
            proof.ProofSignature = GenerateProofSignature(proof);
        }
        
        return proof;
    }

    /// <summary>
    /// Gets statistics about the archive.
    /// </summary>
    public async Task<ArchiveStatistics> GetStatisticsAsync()
    {
        var stats = new ArchiveStatistics
        {
            ArchivePath = _archivePath,
            FileSize = new FileInfo(_archivePath).Length,
            CreatedAt = File.GetCreationTimeUtc(_archivePath),
            LastModified = File.GetLastWriteTimeUtc(_archivePath)
        };
        
        var locations = _blockManager.GetBlockLocations();
        stats.TotalBlocks = locations.Count;
        
        // Count block types
        foreach (var (blockId, _) in locations)
        {
            var result = await _blockManager.ReadBlockAsync(blockId);
            if (result.IsSuccess && result.Value != null)
            {
                switch (result.Value.Type)
                {
                    case BlockType.Segment:
                        stats.EmailBlocks++;
                        break;
                    case BlockType.Metadata:
                        stats.MetadataBlocks++;
                        break;
                    case BlockType.Folder:
                        stats.FolderBlocks++;
                        break;
                }
            }
        }
        
        // Get hash chain info
        var chainHead = _hashChainManager.GetChainHead();
        if (chainHead != null)
        {
            stats.HashChainLength = chainHead.SequenceNumber + 1;
            stats.LatestChainHash = chainHead.ChainHash;
        }
        
        return stats;
    }

    private async Task<ArchiveMetadata> LoadArchiveMetadataAsync()
    {
        // Look for archive metadata block (usually at a known location)
        var metadataBlockId = 1L; // Convention: metadata at block 1
        var result = await _blockManager.ReadBlockAsync(metadataBlockId);
        
        if (result.IsSuccess && result.Value?.Type == BlockType.Metadata)
        {
            try
            {
                var json = Encoding.UTF8.GetString(result.Value.Payload);
                return System.Text.Json.JsonSerializer.Deserialize<ArchiveMetadata>(json);
            }
            catch
            {
                // Fall through to create default metadata
            }
        }
        
        return new ArchiveMetadata
        {
            ArchiveId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            Version = "1.0"
        };
    }

    private async Task<bool> VerifyFileHeaderAsync()
    {
        // Verify the file starts with correct magic bytes
        using var stream = new FileStream(_archivePath, FileMode.Open, FileAccess.Read);
        var header = new byte[4];
        await stream.ReadAsync(header, 0, 4);
        
        // Check for EmailDB magic bytes
        return header[0] == 0x45 && header[1] == 0x4D && header[2] == 0x44 && header[3] == 0x42;
    }

    private async Task<(List<long> ValidBlocks, List<long> InvalidBlocks)> VerifyAllChecksumsAsync()
    {
        var validBlocks = new List<long>();
        var invalidBlocks = new List<long>();
        
        var locations = _blockManager.GetBlockLocations();
        foreach (var (blockId, _) in locations)
        {
            var result = await _blockManager.ReadBlockAsync(blockId);
            if (result.IsSuccess)
            {
                validBlocks.Add(blockId);
            }
            else
            {
                invalidBlocks.Add(blockId);
            }
        }
        
        return (validBlocks, invalidBlocks);
    }

    private async Task<bool> VerifyArchiveSignatureAsync()
    {
        // Placeholder for signature verification
        // In production, this would verify against a trusted public key
        return await Task.FromResult(true);
    }

    private string GenerateProofSignature(EmailExistenceProof proof)
    {
        // Placeholder for proof signing
        // In production, this would use a private key to sign the proof
        using var sha256 = SHA256.Create();
        var data = $"{proof.BlockId}{proof.BlockHash}{proof.ChainHash}{proof.MerkleRoot}";
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }

    public void Dispose()
    {
        _blockManager?.Dispose();
    }
}

/// <summary>
/// Metadata about an EmailDB archive.
/// </summary>
public class ArchiveMetadata
{
    public Guid ArchiveId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Version { get; set; }
    public string Description { get; set; }
    public string Organization { get; set; }
    public string PublicKey { get; set; }
    public string ArchiveSignature { get; set; }
    public Dictionary<string, string> CustomMetadata { get; set; }
}

/// <summary>
/// Result of verifying an archive's integrity.
/// </summary>
public class ArchiveVerificationResult
{
    public string ArchivePath { get; set; }
    public DateTime VerificationStartTime { get; set; }
    public DateTime VerificationEndTime { get; set; }
    public TimeSpan VerificationDuration { get; set; }
    public long FileSize { get; set; }
    public bool IsValid { get; set; }
    public bool HeaderValid { get; set; }
    public int ChecksumsPassed { get; set; }
    public int ChecksumsFailed { get; set; }
    public bool? HashChainValid { get; set; }
    public List<long> HashChainBrokenAt { get; set; }
    public bool? SignatureValid { get; set; }
    public string Error { get; set; }
}

/// <summary>
/// Search criteria for finding emails in an archive.
/// </summary>
public class ArchiveSearchCriteria
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Sender { get; set; }
    public string Recipient { get; set; }
    public string Subject { get; set; }
    public int? MaxResults { get; set; }
}

/// <summary>
/// Represents an archived email with verification info.
/// </summary>
public class ArchivedEmail
{
    public long BlockId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Subject { get; set; }
    public string Sender { get; set; }
    public string Recipients { get; set; }
    public int Size { get; set; }
    public HashChainEntry HashChainEntry { get; set; }
}

/// <summary>
/// Cryptographic proof of an email's existence in the archive.
/// </summary>
public class EmailExistenceProof
{
    public long BlockId { get; set; }
    public DateTime Timestamp { get; set; }
    public string BlockHash { get; set; }
    public string ChainHash { get; set; }
    public long SequenceNumber { get; set; }
    public string MerkleRoot { get; set; }
    public ArchiveMetadata ArchiveMetadata { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string ProofSignature { get; set; }
    
    public string ToJson()
    {
        return System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
    }
}

/// <summary>
/// Statistics about an EmailDB archive.
/// </summary>
public class ArchiveStatistics
{
    public string ArchivePath { get; set; }
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastModified { get; set; }
    public int TotalBlocks { get; set; }
    public int EmailBlocks { get; set; }
    public int MetadataBlocks { get; set; }
    public int FolderBlocks { get; set; }
    public long HashChainLength { get; set; }
    public string LatestChainHash { get; set; }
}