# Phase 2 Implementation Plan: Manager Layer

## Overview
Phase 2 focuses on implementing the manager layer that coordinates between the low-level block storage (Phase 1) and high-level operations. The key goal is to refactor existing managers to use append-only blocks for all data storage while maintaining ZoneTree for indexes only.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                     High-Level API Layer                         │
├─────────────────────────────────────────────────────────────────┤
│ EmailManager (new)                                               │
│ ├── Coordinates all operations                                   │
│ ├── Transaction-like semantics                                   │
│ └── High-level email operations                                 │
└─────────────────────────────────────────────────────────────────┘
                                ↓
┌─────────────────────────────────────────────────────────────────┐
│                     Manager Layer (refactored)                   │
├─────────────────────────────────────────────────────────────────┤
│ HybridEmailStore (enhanced)                                      │
│ ├── Email batching via EmailStorageManager                       │
│ ├── Delegates folder ops to FolderManager                        │
│ └── Maintains indexes in ZoneTree                               │
│                                                                  │
│ FolderManager (refactored)                                       │
│ ├── Stores folders in blocks (not cache)                         │
│ ├── Manages envelope blocks                                      │
│ └── Tracks superseded blocks                                     │
└─────────────────────────────────────────────────────────────────┘
                                ↓
┌─────────────────────────────────────────────────────────────────┐
│                    Storage Layer (Phase 1)                       │
├─────────────────────────────────────────────────────────────────┤
│ EmailStorageManager | RawBlockManager | CacheManager            │
└─────────────────────────────────────────────────────────────────┘
```

## Section 2.1: FolderManager Enhancement

### Current State Analysis
The existing FolderManager:
- Uses CacheManager to store folder data
- Maintains folder tree in memory
- Updates folders through cache operations
- No versioning or superseding support

### Refactoring Strategy
Transform FolderManager to:
1. Store all folder data in append-only blocks
2. Create new block versions instead of updating
3. Maintain envelope blocks for fast listings
4. Track superseded blocks for cleanup

### Task 2.1.1: Add Block Storage Methods
**File**: `EmailDB.Format/FileManagement/FolderManager.cs`
**Dependencies**: RawBlockManager, Phase 1 components
**Description**: Add methods to store folder data in blocks

```csharp
public partial class FolderManager
{
    private readonly RawBlockManager _blockManager;
    private readonly EmailStorageManager _emailStorageManager;
    private readonly IBlockContentSerializer _serializer;
    private readonly Dictionary<string, long> _folderBlockCache = new();
    private readonly Dictionary<string, long> _envelopeBlockCache = new();
    
    // Track superseded blocks
    private readonly List<SupersededBlock> _supersededBlocks = new();
    
    public class SupersededBlock
    {
        public long BlockId { get; set; }
        public BlockType Type { get; set; }
        public DateTime SupersededAt { get; set; }
        public string Reason { get; set; }
    }
    
    /// <summary>
    /// Stores a folder content block and returns the block ID.
    /// </summary>
    public async Task<Result<long>> StoreFolderBlockAsync(FolderContent folder)
    {
        try
        {
            // Increment version
            folder.Version++;
            folder.LastModified = DateTime.UtcNow;
            
            // Serialize folder
            var serialized = _serializer.Serialize(folder, PayloadEncoding.Protobuf);
            if (!serialized.IsSuccess)
                return Result<long>.Failure($"Failed to serialize folder: {serialized.Error}");
            
            // Write as new block
            var blockResult = await _blockManager.WriteBlockAsync(
                BlockType.Folder,
                serialized.Value,
                compression: CompressionAlgorithm.LZ4);
                
            if (!blockResult.IsSuccess)
                return blockResult;
            
            var blockId = blockResult.Value;
            
            // Track old block as superseded if exists
            if (_folderBlockCache.TryGetValue(folder.Name, out var oldBlockId))
            {
                _supersededBlocks.Add(new SupersededBlock
                {
                    BlockId = oldBlockId,
                    Type = BlockType.Folder,
                    SupersededAt = DateTime.UtcNow,
                    Reason = "Folder update"
                });
            }
            
            // Update cache
            _folderBlockCache[folder.Name] = blockId;
            
            return Result<long>.Success(blockId);
        }
        catch (Exception ex)
        {
            return Result<long>.Failure($"Failed to store folder block: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Stores an envelope block for a folder.
    /// </summary>
    public async Task<Result<long>> StoreEnvelopeBlockAsync(FolderEnvelopeBlock envelopeBlock)
    {
        try
        {
            // Increment version
            envelopeBlock.Version++;
            envelopeBlock.LastModified = DateTime.UtcNow;
            
            // Link to previous version if exists
            if (_envelopeBlockCache.TryGetValue(envelopeBlock.FolderPath, out var previousBlockId))
            {
                envelopeBlock.PreviousBlockId = previousBlockId;
            }
            
            // Serialize
            var serialized = _serializer.Serialize(envelopeBlock, PayloadEncoding.Protobuf);
            if (!serialized.IsSuccess)
                return Result<long>.Failure($"Failed to serialize envelope block: {serialized.Error}");
            
            // Write block (no compression for fast access)
            var blockResult = await _blockManager.WriteBlockAsync(
                BlockType.FolderEnvelope,
                serialized.Value,
                compression: CompressionAlgorithm.None);
                
            if (!blockResult.IsSuccess)
                return blockResult;
            
            var blockId = blockResult.Value;
            
            // Track old block as superseded
            if (envelopeBlock.PreviousBlockId.HasValue)
            {
                _supersededBlocks.Add(new SupersededBlock
                {
                    BlockId = envelopeBlock.PreviousBlockId.Value,
                    Type = BlockType.FolderEnvelope,
                    SupersededAt = DateTime.UtcNow,
                    Reason = "Envelope update"
                });
            }
            
            // Update cache
            _envelopeBlockCache[envelopeBlock.FolderPath] = blockId;
            
            return Result<long>.Success(blockId);
        }
        catch (Exception ex)
        {
            return Result<long>.Failure($"Failed to store envelope block: {ex.Message}");
        }
    }
}
```

### Task 2.1.2: Refactor Folder Operations
**File**: `EmailDB.Format/FileManagement/FolderManager.cs` (continued)
**Description**: Update existing methods to use block storage

```csharp
public partial class FolderManager
{
    /// <summary>
    /// Creates a new folder at the specified path.
    /// </summary>
    public new async Task<Result> CreateFolderAsync(string path)
    {
        try
        {
            ValidatePath(path);
            
            // Get folder tree from blocks (not cache)
            var folderTreeResult = await LoadFolderTreeFromBlocksAsync();
            if (!folderTreeResult.IsSuccess)
                return Result.Failure($"Failed to load folder tree: {folderTreeResult.Error}");
                
            var folderTree = folderTreeResult.Value;
            
            // Check if path already exists
            if (folderTree.FolderIDs.ContainsKey(path))
                return Result.Failure($"Folder '{path}' already exists");
            
            var (parentPath, folderName) = SplitPath(path);
            
            // Get parent folder ID
            long parentFolderId = 0;
            if (!string.IsNullOrEmpty(parentPath))
            {
                if (!folderTree.FolderIDs.TryGetValue(parentPath, out parentFolderId))
                    return Result.Failure($"Parent folder '{parentPath}' not found");
            }
            
            // Create new folder
            var folderId = GetNextFolderId(folderTree);
            var folder = new FolderContent
            {
                FolderId = folderId,
                ParentFolderId = parentFolderId,
                Name = path,
                EmailIds = new List<EmailHashedID>(),
                Version = 0
            };
            
            // Store folder in block
            var folderBlockResult = await StoreFolderBlockAsync(folder);
            if (!folderBlockResult.IsSuccess)
                return Result.Failure($"Failed to store folder: {folderBlockResult.Error}");
            
            // Create empty envelope block
            var envelopeBlock = new FolderEnvelopeBlock
            {
                FolderPath = path,
                Version = 0,
                Envelopes = new List<EmailEnvelope>()
            };
            
            var envelopeBlockResult = await StoreEnvelopeBlockAsync(envelopeBlock);
            if (!envelopeBlockResult.IsSuccess)
                return Result.Failure($"Failed to store envelope block: {envelopeBlockResult.Error}");
            
            // Update folder to reference envelope block
            folder.EnvelopeBlockId = envelopeBlockResult.Value;
            
            // Store updated folder
            var updateResult = await StoreFolderBlockAsync(folder);
            if (!updateResult.IsSuccess)
                return Result.Failure($"Failed to update folder with envelope reference: {updateResult.Error}");
            
            // Update folder tree
            folderTree.FolderIDs[path] = folderId;
            folderTree.FolderOffsets[folderId] = folderBlockResult.Value;
            
            // Store updated folder tree
            await StoreFolderTreeBlockAsync(folderTree);
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to create folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Adds an email to a folder and updates the envelope block.
    /// </summary>
    public async Task<Result> AddEmailToFolderAsync(
        string folderPath, 
        EmailHashedID emailId,
        EmailEnvelope envelope)
    {
        try
        {
            // Load folder
            var folderResult = await LoadFolderAsync(folderPath);
            if (!folderResult.IsSuccess)
                return Result.Failure($"Failed to load folder: {folderResult.Error}");
                
            var folder = folderResult.Value;
            
            // Add email ID to folder
            folder.EmailIds.Add(emailId);
            
            // Load envelope block
            var envelopeResult = await LoadEnvelopeBlockAsync(folder.EnvelopeBlockId);
            if (!envelopeResult.IsSuccess)
                return Result.Failure($"Failed to load envelope block: {envelopeResult.Error}");
                
            var envelopeBlock = envelopeResult.Value;
            
            // Add envelope
            envelope.CompoundId = emailId.ToCompoundKey();
            envelopeBlock.Envelopes.Add(envelope);
            
            // Store updated envelope block
            var newEnvelopeBlockResult = await StoreEnvelopeBlockAsync(envelopeBlock);
            if (!newEnvelopeBlockResult.IsSuccess)
                return Result.Failure($"Failed to store envelope block: {newEnvelopeBlockResult.Error}");
            
            // Update folder with new envelope block reference
            folder.EnvelopeBlockId = newEnvelopeBlockResult.Value;
            
            // Store updated folder
            var folderBlockResult = await StoreFolderBlockAsync(folder);
            if (!folderBlockResult.IsSuccess)
                return Result.Failure($"Failed to store folder: {folderBlockResult.Error}");
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to add email to folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets folder listing by loading the envelope block.
    /// </summary>
    public async Task<Result<List<EmailEnvelope>>> GetFolderListingAsync(string folderPath)
    {
        try
        {
            // Check cache first
            if (_envelopeBlockCache.TryGetValue(folderPath, out var cachedBlockId))
            {
                var cachedResult = await LoadEnvelopeBlockAsync(cachedBlockId);
                if (cachedResult.IsSuccess)
                    return Result<List<EmailEnvelope>>.Success(cachedResult.Value.Envelopes);
            }
            
            // Load folder
            var folderResult = await LoadFolderAsync(folderPath);
            if (!folderResult.IsSuccess)
                return Result<List<EmailEnvelope>>.Failure($"Failed to load folder: {folderResult.Error}");
                
            var folder = folderResult.Value;
            
            // Load envelope block
            var envelopeResult = await LoadEnvelopeBlockAsync(folder.EnvelopeBlockId);
            if (!envelopeResult.IsSuccess)
                return Result<List<EmailEnvelope>>.Failure($"Failed to load envelope block: {envelopeResult.Error}");
            
            return Result<List<EmailEnvelope>>.Success(envelopeResult.Value.Envelopes);
        }
        catch (Exception ex)
        {
            return Result<List<EmailEnvelope>>.Failure($"Failed to get folder listing: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets list of superseded blocks for cleanup.
    /// </summary>
    public async Task<Result<List<SupersededBlock>>> GetSupersededBlocksAsync()
    {
        // In production, this would query from a persistent store
        // For now, return from in-memory list
        var oldBlocks = _supersededBlocks
            .Where(b => (DateTime.UtcNow - b.SupersededAt).TotalHours > 24)
            .ToList();
            
        return Result<List<SupersededBlock>>.Success(oldBlocks);
    }
}
```

## Section 2.2: Create EmailManager

### Task 2.2.1: Define EmailManager Interface
**File**: `EmailDB.Format/FileManagement/IEmailManager.cs`
**Description**: High-level interface for email operations

```csharp
namespace EmailDB.Format.FileManagement;

public interface IEmailManager : IDisposable
{
    // Email operations
    Task<Result<EmailHashedID>> ImportEMLAsync(string emlContent, string folderPath = "Inbox");
    Task<Result<EmailHashedID>> ImportEMLFileAsync(string filePath, string folderPath = "Inbox");
    Task<Result<BatchImportResult>> ImportEMLBatchAsync((string fileName, string emlContent)[] emails, string folderPath = "Inbox");
    
    // Retrieval operations
    Task<Result<MimeMessage>> GetEmailAsync(EmailHashedID emailId);
    Task<Result<MimeMessage>> GetEmailByMessageIdAsync(string messageId);
    Task<Result<List<EmailEnvelope>>> GetFolderListingAsync(string folderPath);
    
    // Search operations
    Task<Result<List<EmailSearchResult>>> SearchAsync(string searchTerm, int maxResults = 50);
    Task<Result<List<EmailSearchResult>>> AdvancedSearchAsync(SearchQuery query);
    
    // Folder operations
    Task<Result> CreateFolderAsync(string folderPath);
    Task<Result> MoveEmailAsync(EmailHashedID emailId, string fromFolder, string toFolder);
    Task<Result> DeleteEmailAsync(EmailHashedID emailId, bool permanent = false);
    
    // Database operations
    Task<Result<DatabaseStats>> GetDatabaseStatsAsync();
    Task<Result> OptimizeDatabaseAsync();
}

public class SearchQuery
{
    public string Subject { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string[] Keywords { get; set; }
    public string Folder { get; set; }
}

public class EmailSearchResult
{
    public EmailHashedID EmailId { get; set; }
    public EmailEnvelope Envelope { get; set; }
    public float RelevanceScore { get; set; }
    public List<string> MatchedFields { get; set; }
}

public class BatchImportResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<EmailHashedID> ImportedEmailIds { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class DatabaseStats
{
    public long TotalEmails { get; set; }
    public long TotalBlocks { get; set; }
    public long DatabaseSize { get; set; }
    public int FolderCount { get; set; }
    public Dictionary<string, long> BlockTypeCounts { get; set; }
    public double CompressionRatio { get; set; }
}
```

### Task 2.2.2: Implement EmailManager
**File**: `EmailDB.Format/FileManagement/EmailManager.cs`
**Dependencies**: HybridEmailStore, FolderManager, EmailStorageManager
**Description**: High-level coordinator for all email operations

```csharp
namespace EmailDB.Format.FileManagement;

public class EmailManager : IEmailManager
{
    private readonly HybridEmailStore _hybridStore;
    private readonly FolderManager _folderManager;
    private readonly EmailStorageManager _storageManager;
    private readonly RawBlockManager _blockManager;
    private readonly IBlockContentSerializer _serializer;
    private bool _disposed;
    
    // Transaction support
    private readonly AsyncLocal<EmailTransaction> _currentTransaction = new();
    
    public EmailManager(
        HybridEmailStore hybridStore,
        FolderManager folderManager,
        EmailStorageManager storageManager,
        RawBlockManager blockManager,
        IBlockContentSerializer serializer)
    {
        _hybridStore = hybridStore ?? throw new ArgumentNullException(nameof(hybridStore));
        _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }
    
    /// <summary>
    /// Imports an EML file with full transaction support.
    /// </summary>
    public async Task<Result<EmailHashedID>> ImportEMLAsync(string emlContent, string folderPath = "Inbox")
    {
        using var transaction = BeginTransaction();
        
        try
        {
            // Parse email
            var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(emlContent)));
            var emailData = Encoding.UTF8.GetBytes(emlContent);
            
            // Store email in blocks
            var storeResult = await _storageManager.StoreEmailAsync(message, emailData);
            if (!storeResult.IsSuccess)
                return Result<EmailHashedID>.Failure($"Failed to store email: {storeResult.Error}");
            
            var emailId = storeResult.Value;
            transaction.RecordAction(async () => await RollbackEmailStorageAsync(emailId));
            
            // Create envelope
            var envelope = new EmailEnvelope
            {
                MessageId = message.MessageId,
                Subject = message.Subject,
                From = message.From?.ToString() ?? "",
                To = message.To?.ToString() ?? "",
                Date = message.Date.DateTime,
                Size = emailData.Length,
                HasAttachments = message.Attachments.Any(),
                EnvelopeHash = emailId.EnvelopeHash
            };
            
            // Add to folder
            var folderResult = await _folderManager.AddEmailToFolderAsync(folderPath, emailId, envelope);
            if (!folderResult.IsSuccess)
            {
                await transaction.RollbackAsync();
                return Result<EmailHashedID>.Failure($"Failed to add email to folder: {folderResult.Error}");
            }
            transaction.RecordAction(async () => await _folderManager.RemoveEmailFromFolderAsync(folderPath, emailId));
            
            // Update indexes via HybridStore
            var indexResult = await _hybridStore.UpdateIndexesForEmailAsync(emailId, message, folderPath);
            if (!indexResult.IsSuccess)
            {
                await transaction.RollbackAsync();
                return Result<EmailHashedID>.Failure($"Failed to update indexes: {indexResult.Error}");
            }
            
            await transaction.CommitAsync();
            return Result<EmailHashedID>.Success(emailId);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return Result<EmailHashedID>.Failure($"Failed to import email: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Batch import with optimized performance.
    /// </summary>
    public async Task<Result<BatchImportResult>> ImportEMLBatchAsync(
        (string fileName, string emlContent)[] emails, 
        string folderPath = "Inbox")
    {
        var result = new BatchImportResult();
        
        // Process in batches to optimize block usage
        const int batchSize = 100;
        for (int i = 0; i < emails.Length; i += batchSize)
        {
            var batch = emails.Skip(i).Take(batchSize).ToArray();
            
            using var transaction = BeginTransaction();
            
            try
            {
                foreach (var (fileName, emlContent) in batch)
                {
                    var importResult = await ImportEMLAsync(emlContent, folderPath);
                    if (importResult.IsSuccess)
                    {
                        result.SuccessCount++;
                        result.ImportedEmailIds.Add(importResult.Value);
                    }
                    else
                    {
                        result.ErrorCount++;
                        result.Errors.Add($"{fileName}: {importResult.Error}");
                    }
                }
                
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                result.Errors.Add($"Batch error: {ex.Message}");
            }
        }
        
        // Flush any pending emails
        await _storageManager.FlushPendingEmailsAsync();
        
        return Result<BatchImportResult>.Success(result);
    }
    
    /// <summary>
    /// Retrieves an email by its ID.
    /// </summary>
    public async Task<Result<MimeMessage>> GetEmailAsync(EmailHashedID emailId)
    {
        try
        {
            // Get block location from index
            var blockResult = await _hybridStore.GetEmailBlockLocationAsync(emailId.ToCompoundKey());
            if (!blockResult.IsSuccess)
                return Result<MimeMessage>.Failure($"Email not found: {blockResult.Error}");
            
            var blockLocation = blockResult.Value;
            
            // Read block
            var block = await _blockManager.ReadBlockAsync(blockLocation.BlockId);
            if (!block.IsSuccess)
                return Result<MimeMessage>.Failure($"Failed to read block: {block.Error}");
            
            // Extract email from block
            var emails = DeserializeEmailBlock(block.Value.Payload);
            if (blockLocation.LocalId >= emails.Count)
                return Result<MimeMessage>.Failure("Invalid local ID in block");
            
            var emailData = emails[blockLocation.LocalId];
            var message = MimeMessage.Load(new MemoryStream(emailData));
            
            return Result<MimeMessage>.Success(message);
        }
        catch (Exception ex)
        {
            return Result<MimeMessage>.Failure($"Failed to retrieve email: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets folder listing efficiently using envelope blocks.
    /// </summary>
    public async Task<Result<List<EmailEnvelope>>> GetFolderListingAsync(string folderPath)
    {
        return await _folderManager.GetFolderListingAsync(folderPath);
    }
    
    /// <summary>
    /// Searches emails using full-text index.
    /// </summary>
    public async Task<Result<List<EmailSearchResult>>> SearchAsync(string searchTerm, int maxResults = 50)
    {
        try
        {
            // Use HybridStore's search functionality
            var searchResults = await _hybridStore.SearchEmailsAsync(searchTerm, maxResults);
            
            var results = new List<EmailSearchResult>();
            foreach (var (emailId, score) in searchResults)
            {
                // Get envelope for quick metadata
                var envelopeResult = await GetEmailEnvelopeAsync(emailId);
                if (envelopeResult.IsSuccess)
                {
                    results.Add(new EmailSearchResult
                    {
                        EmailId = EmailHashedID.FromCompoundKey(emailId),
                        Envelope = envelopeResult.Value,
                        RelevanceScore = score,
                        MatchedFields = new List<string> { "content" }
                    });
                }
            }
            
            return Result<List<EmailSearchResult>>.Success(results);
        }
        catch (Exception ex)
        {
            return Result<List<EmailSearchResult>>.Failure($"Search failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Moves an email between folders atomically.
    /// </summary>
    public async Task<Result> MoveEmailAsync(
        EmailHashedID emailId, 
        string fromFolder, 
        string toFolder)
    {
        using var transaction = BeginTransaction();
        
        try
        {
            // Get envelope from source folder
            var sourceListingResult = await _folderManager.GetFolderListingAsync(fromFolder);
            if (!sourceListingResult.IsSuccess)
                return Result.Failure($"Failed to get source folder: {sourceListingResult.Error}");
            
            var envelope = sourceListingResult.Value
                .FirstOrDefault(e => e.CompoundId == emailId.ToCompoundKey());
                
            if (envelope == null)
                return Result.Failure("Email not found in source folder");
            
            // Remove from source folder
            var removeResult = await _folderManager.RemoveEmailFromFolderAsync(fromFolder, emailId);
            if (!removeResult.IsSuccess)
            {
                await transaction.RollbackAsync();
                return removeResult;
            }
            transaction.RecordAction(async () => 
                await _folderManager.AddEmailToFolderAsync(fromFolder, emailId, envelope));
            
            // Add to destination folder
            var addResult = await _folderManager.AddEmailToFolderAsync(toFolder, emailId, envelope);
            if (!addResult.IsSuccess)
            {
                await transaction.RollbackAsync();
                return addResult;
            }
            transaction.RecordAction(async () => 
                await _folderManager.RemoveEmailFromFolderAsync(toFolder, emailId));
            
            // Update indexes
            var indexResult = await _hybridStore.UpdateEmailFolderAsync(emailId.ToCompoundKey(), fromFolder, toFolder);
            if (!indexResult.IsSuccess)
            {
                await transaction.RollbackAsync();
                return indexResult;
            }
            
            await transaction.CommitAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return Result.Failure($"Failed to move email: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets database statistics.
    /// </summary>
    public async Task<Result<DatabaseStats>> GetDatabaseStatsAsync()
    {
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            var blockTypeCounts = blockLocations
                .GroupBy(b => b.Value.Type)
                .ToDictionary(g => g.Key.ToString(), g => (long)g.Count());
            
            var stats = new DatabaseStats
            {
                TotalBlocks = blockLocations.Count,
                BlockTypeCounts = blockTypeCounts,
                DatabaseSize = new FileInfo(_blockManager.FilePath).Length,
                // Additional stats would be calculated here
            };
            
            return Result<DatabaseStats>.Success(stats);
        }
        catch (Exception ex)
        {
            return Result<DatabaseStats>.Failure($"Failed to get database stats: {ex.Message}");
        }
    }
    
    // Transaction support
    private EmailTransaction BeginTransaction()
    {
        var transaction = new EmailTransaction();
        _currentTransaction.Value = transaction;
        return transaction;
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Simple transaction support for multi-block operations.
/// </summary>
internal class EmailTransaction : IDisposable
{
    private readonly Stack<Func<Task>> _rollbackActions = new();
    private bool _committed;
    
    public void RecordAction(Func<Task> rollbackAction)
    {
        _rollbackActions.Push(rollbackAction);
    }
    
    public async Task CommitAsync()
    {
        _committed = true;
        _rollbackActions.Clear();
    }
    
    public async Task RollbackAsync()
    {
        while (_rollbackActions.Count > 0)
        {
            var action = _rollbackActions.Pop();
            try
            {
                await action();
            }
            catch
            {
                // Log rollback failures but continue
            }
        }
    }
    
    public void Dispose()
    {
        if (!_committed && _rollbackActions.Count > 0)
        {
            // Force synchronous rollback on dispose
            Task.Run(async () => await RollbackAsync()).Wait();
        }
    }
}
```

## Section 2.3: Update HybridEmailStore

### Task 2.3.1: Refactor HybridEmailStore
**File**: `EmailDB.Format/FileManagement/HybridEmailStore.cs`
**Description**: Update to delegate folder operations to FolderManager

```csharp
public partial class HybridEmailStore
{
    private readonly FolderManager _folderManager;
    private readonly EmailStorageManager _storageManager;
    
    // Update constructor
    public HybridEmailStore(
        string dataPath, 
        string indexDirectory,
        FolderManager folderManager,
        EmailStorageManager storageManager,
        int blockSizeThreshold = 1024 * 1024)
    {
        _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        
        // Remove _blockStore initialization - use EmailStorageManager instead
        // Remove direct folder storage in ZoneTree - use FolderManager instead
        
        _indexDirectory = indexDirectory;
        Directory.CreateDirectory(indexDirectory);
        
        // Initialize indexes (store references only)
        _messageIdIndex = new ZoneTreeFactory<string, string>()
            .SetDataDirectory(Path.Combine(indexDirectory, "message_id"))
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new Utf8StringSerializer())
            .OpenOrCreate();
            
        // Change folder index to store block locations
        _folderPathIndex = new ZoneTreeFactory<string, long>()
            .SetDataDirectory(Path.Combine(indexDirectory, "folder_paths"))
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new Int64Serializer())
            .OpenOrCreate();
            
        // Add email location index
        _emailLocationIndex = new ZoneTreeFactory<string, BlockLocation>()
            .SetDataDirectory(Path.Combine(indexDirectory, "email_locations"))
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new BlockLocationSerializer())
            .OpenOrCreate();
            
        // Keep other indexes...
    }
    
    /// <summary>
    /// Updates indexes for a newly stored email.
    /// </summary>
    public async Task<Result> UpdateIndexesForEmailAsync(
        EmailHashedID emailId,
        MimeMessage message,
        string folderPath)
    {
        try
        {
            var compoundKey = emailId.ToCompoundKey();
            
            // Update message ID index
            _messageIdIndex.Upsert(message.MessageId, compoundKey);
            
            // Update envelope hash index (for deduplication)
            _envelopeHashIndex.Upsert(
                Convert.ToBase64String(emailId.EnvelopeHash), 
                compoundKey);
            
            // Update content hash index
            _contentHashIndex.Upsert(
                Convert.ToBase64String(emailId.ContentHash), 
                compoundKey);
            
            // Update email location index
            _emailLocationIndex.Upsert(compoundKey, new BlockLocation
            {
                BlockId = emailId.BlockId,
                LocalId = emailId.LocalId
            });
            
            // Update full-text search index
            await UpdateFullTextIndexAsync(compoundKey, message);
            
            // Note: Folder associations are handled by FolderManager
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update indexes: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets the block location for an email.
    /// </summary>
    public async Task<Result<BlockLocation>> GetEmailBlockLocationAsync(string compoundKey)
    {
        if (_emailLocationIndex.TryGet(compoundKey, out var location))
        {
            return Result<BlockLocation>.Success(location);
        }
        
        return Result<BlockLocation>.Failure("Email not found in index");
    }
    
    /// <summary>
    /// Searches emails using the full-text index.
    /// </summary>
    public async Task<List<(string compoundKey, float score)>> SearchEmailsAsync(
        string searchTerm, 
        int maxResults)
    {
        var results = new Dictionary<string, float>();
        var searchTermLower = searchTerm.ToLowerInvariant();
        var words = ExtractWords(searchTermLower);
        
        foreach (var word in words)
        {
            if (_fullTextIndex.TryGet(word, out var emailIds))
            {
                foreach (var emailId in emailIds)
                {
                    if (!results.ContainsKey(emailId))
                        results[emailId] = 0;
                    
                    results[emailId] += 1.0f / words.Count; // Simple TF scoring
                }
            }
        }
        
        return results
            .OrderByDescending(r => r.Value)
            .Take(maxResults)
            .Select(r => (r.Key, r.Value))
            .ToList();
    }
    
    /// <summary>
    /// Updates folder association for an email.
    /// </summary>
    public async Task<Result> UpdateEmailFolderAsync(
        string compoundKey, 
        string fromFolder, 
        string toFolder)
    {
        try
        {
            // Update metadata index with new folder
            if (_metadataIndex.TryGet(compoundKey, out var metadata))
            {
                metadata.Folder = toFolder;
                _metadataIndex.Upsert(compoundKey, metadata);
                return Result.Success();
            }
            
            return Result.Failure("Email metadata not found");
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update email folder: {ex.Message}");
        }
    }
}

// Helper classes
public class BlockLocation
{
    public long BlockId { get; set; }
    public int LocalId { get; set; }
}

public class BlockLocationSerializer : ISerializer<BlockLocation>
{
    public BlockLocation Deserialize(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);
        return new BlockLocation
        {
            BlockId = reader.ReadInt64(),
            LocalId = reader.ReadInt32()
        };
    }
    
    public byte[] Serialize(in BlockLocation value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(value.BlockId);
        writer.Write(value.LocalId);
        return ms.ToArray();
    }
}
```

### Task 2.3.2: Implement Atomic Multi-Block Updates
**File**: `EmailDB.Format/FileManagement/HybridEmailStore.cs` (continued)
**Description**: Add support for atomic operations across multiple blocks

```csharp
public partial class HybridEmailStore
{
    /// <summary>
    /// Performs an atomic multi-block update operation.
    /// </summary>
    public async Task<Result> ExecuteAtomicUpdateAsync(
        Func<AtomicUpdateContext, Task<Result>> updateAction)
    {
        var context = new AtomicUpdateContext(this);
        
        try
        {
            // Execute the update action
            var result = await updateAction(context);
            
            if (!result.IsSuccess)
            {
                // Rollback any pending changes
                await context.RollbackAsync();
                return result;
            }
            
            // Commit all changes
            await context.CommitAsync();
            return Result.Success();
        }
        catch (Exception ex)
        {
            await context.RollbackAsync();
            return Result.Failure($"Atomic update failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Context for atomic multi-block updates.
/// </summary>
public class AtomicUpdateContext
{
    private readonly HybridEmailStore _store;
    private readonly List<IndexUpdate> _pendingUpdates = new();
    private readonly List<Func<Task>> _rollbackActions = new();
    
    internal AtomicUpdateContext(HybridEmailStore store)
    {
        _store = store;
    }
    
    public void AddIndexUpdate(string indexName, string key, object value)
    {
        _pendingUpdates.Add(new IndexUpdate
        {
            IndexName = indexName,
            Key = key,
            Value = value
        });
    }
    
    public void AddRollbackAction(Func<Task> action)
    {
        _rollbackActions.Add(action);
    }
    
    internal async Task CommitAsync()
    {
        // Apply all index updates
        foreach (var update in _pendingUpdates)
        {
            // Apply update based on index name
            // This would be implemented based on actual index types
        }
        
        _pendingUpdates.Clear();
        _rollbackActions.Clear();
    }
    
    internal async Task RollbackAsync()
    {
        // Execute rollback actions in reverse order
        for (int i = _rollbackActions.Count - 1; i >= 0; i--)
        {
            try
            {
                await _rollbackActions[i]();
            }
            catch
            {
                // Log but continue rollback
            }
        }
        
        _pendingUpdates.Clear();
        _rollbackActions.Clear();
    }
    
    private class IndexUpdate
    {
        public string IndexName { get; set; }
        public string Key { get; set; }
        public object Value { get; set; }
    }
}
```

## Implementation Timeline

### Week 1: FolderManager Refactoring (Days 1-5)
**Day 1-2: Block Storage Implementation**
- [ ] Task 2.1.1: Add block storage methods to FolderManager
- [ ] Implement superseded block tracking
- [ ] Create envelope block management

**Day 3-4: Refactor Operations**
- [ ] Task 2.1.2: Update CreateFolderAsync to use blocks
- [ ] Implement AddEmailToFolderAsync with envelope updates
- [ ] Create GetFolderListingAsync using envelope blocks

**Day 5: Testing & Optimization**
- [ ] Unit tests for new FolderManager methods
- [ ] Performance tests for envelope block access
- [ ] Optimize caching strategy

### Week 2: EmailManager Implementation (Days 6-10)
**Day 6-7: Core Structure**
- [ ] Task 2.2.1: Define IEmailManager interface
- [ ] Task 2.2.2: Implement basic EmailManager structure
- [ ] Set up dependency injection

**Day 8-9: Core Operations**
- [ ] Implement ImportEMLAsync with transactions
- [ ] Create batch import functionality
- [ ] Implement email retrieval methods

**Day 10: Advanced Operations**
- [ ] Implement search functionality
- [ ] Create MoveEmailAsync with atomic updates
- [ ] Add database statistics

### Week 3: HybridEmailStore Updates (Days 11-15)
**Day 11-12: Refactoring**
- [ ] Task 2.3.1: Remove direct folder storage
- [ ] Update indexes to store references only
- [ ] Integrate with FolderManager

**Day 13-14: Atomic Updates**
- [ ] Task 2.3.2: Implement atomic update context
- [ ] Add transaction support
- [ ] Create rollback mechanisms

**Day 15: Integration Testing**
- [ ] End-to-end email import tests
- [ ] Folder operations tests
- [ ] Search functionality tests
- [ ] Performance benchmarks

## Integration Points

### With Phase 1 Components
1. **EmailStorageManager**: Used for batching emails into blocks
2. **RawBlockManager**: Enhanced with compression/encryption
3. **Block Types**: New envelope and folder blocks
4. **Key Management**: Encryption keys for secure storage

### With Existing Code
1. **CacheManager**: Modified to cache envelope blocks
2. **MetadataManager**: Updated to track folder block locations
3. **ZoneTree**: Indexes modified to store references only

## Success Criteria

1. **All folder data stored in blocks** (not in ZoneTree)
2. **Envelope blocks enable fast folder listings** (<10ms for 1000 emails)
3. **Atomic operations** prevent partial updates
4. **Transaction support** ensures consistency
5. **Backward compatibility** maintained where possible
6. **Performance targets**:
   - Folder listing: <10ms
   - Email import: >1000 emails/second
   - Search: <100ms for 1M emails

## Risk Mitigation

1. **Data Migration**: Create tools to migrate existing data
2. **Performance**: Extensive caching for envelope blocks
3. **Consistency**: Transaction support with rollback
4. **Compatibility**: Adapter layer for existing code
5. **Testing**: Comprehensive test suite before integration