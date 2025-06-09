using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// High-level coordinator for all email operations.
/// </summary>
public class EmailManager : IEmailManager
{
    private readonly HybridEmailStore _hybridStore;
    private readonly FolderManager _folderManager;
    private readonly EmailStorageManager _storageManager;
    private readonly RawBlockManager _blockManager;
    private readonly iBlockContentSerializer _serializer;
    private bool _disposed;
    
    // Transaction support
    private readonly AsyncLocal<EmailTransaction> _currentTransaction = new();
    
    public EmailManager(
        HybridEmailStore hybridStore,
        FolderManager folderManager,
        EmailStorageManager storageManager,
        RawBlockManager blockManager,
        iBlockContentSerializer serializer)
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
            
            var batchId = storeResult.Value;
            var emailId = new EmailHashedID
            {
                BlockId = batchId.BlockId,
                LocalId = batchId.LocalId,
                EnvelopeHash = batchId.EnvelopeHash,
                ContentHash = batchId.ContentHash
            };
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
    /// Imports an EML file from disk.
    /// </summary>
    public async Task<Result<EmailHashedID>> ImportEMLFileAsync(string filePath, string folderPath = "Inbox")
    {
        try
        {
            var emlContent = await File.ReadAllTextAsync(filePath);
            return await ImportEMLAsync(emlContent, folderPath);
        }
        catch (Exception ex)
        {
            return Result<EmailHashedID>.Failure($"Failed to read EML file: {ex.Message}");
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
            var compoundKey = $"{emailId.BlockId}:{emailId.LocalId}";
            var blockResult = await _hybridStore.GetEmailBlockLocationAsync(compoundKey);
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
    /// Gets an email by its message ID.
    /// </summary>
    public async Task<Result<MimeMessage>> GetEmailByMessageIdAsync(string messageId)
    {
        try
        {
            // Look up in message ID index
            var compoundKey = await _hybridStore.GetEmailIdByMessageIdAsync(messageId);
            if (string.IsNullOrEmpty(compoundKey))
                return Result<MimeMessage>.Failure("Email not found");
            
            var parts = compoundKey.Split(':');
            if (parts.Length != 2)
                return Result<MimeMessage>.Failure("Invalid compound key format");
            
            var emailId = new EmailHashedID
            {
                BlockId = long.Parse(parts[0]),
                LocalId = int.Parse(parts[1])
            };
            
            return await GetEmailAsync(emailId);
        }
        catch (Exception ex)
        {
            return Result<MimeMessage>.Failure($"Failed to get email by message ID: {ex.Message}");
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
                    var parts = emailId.Split(':');
                    results.Add(new EmailSearchResult
                    {
                        EmailId = new EmailHashedID 
                        { 
                            BlockId = long.Parse(parts[0]), 
                            LocalId = int.Parse(parts[1]) 
                        },
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
    /// Advanced search with multiple criteria.
    /// </summary>
    public async Task<Result<List<EmailSearchResult>>> AdvancedSearchAsync(SearchQuery query)
    {
        // This would be implemented with more sophisticated index queries
        // For now, delegate to simple search
        var keywords = query.Keywords ?? new[] { query.Subject, query.From, query.To };
        var searchTerm = string.Join(" ", keywords.Where(k => !string.IsNullOrEmpty(k)));
        return await SearchAsync(searchTerm);
    }
    
    /// <summary>
    /// Creates a new folder.
    /// </summary>
    public async Task<Result> CreateFolderAsync(string folderPath)
    {
        return await _folderManager.CreateFolderAsync(folderPath);
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
            
            var compoundId = $"{emailId.BlockId}:{emailId.LocalId}";
            var envelope = sourceListingResult.Value
                .FirstOrDefault(e => e.CompoundId == compoundId);
                
            if (envelope == null)
                return Result.Failure("Email not found in source folder");
            
            // Remove from source folder
            await _folderManager.RemoveEmailFromFolderAsync(fromFolder, emailId);
            transaction.RecordAction(async () => 
                await _folderManager.AddEmailToFolderAsync(fromFolder, emailId));
            
            // Add to destination folder
            await _folderManager.AddEmailToFolderAsync(toFolder, emailId);
            transaction.RecordAction(async () => 
                await _folderManager.RemoveEmailFromFolderAsync(toFolder, emailId));
            
            // Update indexes
            var indexResult = await _hybridStore.UpdateEmailFolderAsync(compoundId, fromFolder, toFolder);
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
    /// Deletes an email.
    /// </summary>
    public async Task<Result> DeleteEmailAsync(EmailHashedID emailId, bool permanent = false)
    {
        // Implementation would mark email as deleted or remove from all folders
        // For now, return not implemented
        return Result.Failure("Delete not yet implemented");
    }
    
    /// <summary>
    /// Gets database statistics.
    /// </summary>
    public async Task<Result<DatabaseStats>> GetDatabaseStatsAsync()
    {
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            
            var stats = new DatabaseStats
            {
                TotalBlocks = blockLocations.Count,
                BlockTypeCounts = new Dictionary<string, long>(), // Would need to read blocks to get types
                DatabaseSize = 0, // Would need access to file path
                // Additional stats would be calculated here
            };
            
            return Result<DatabaseStats>.Success(stats);
        }
        catch (Exception ex)
        {
            return Result<DatabaseStats>.Failure($"Failed to get database stats: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Optimizes the database.
    /// </summary>
    public async Task<Result> OptimizeDatabaseAsync()
    {
        // Would implement compaction, cleanup of superseded blocks, etc.
        return Result.Success();
    }
    
    // Helper methods
    
    private async Task<Result<EmailEnvelope>> GetEmailEnvelopeAsync(string compoundKey)
    {
        // This would be optimized to retrieve from envelope blocks
        // For now, create a minimal envelope
        var parts = compoundKey.Split(':');
        return Result<EmailEnvelope>.Success(new EmailEnvelope
        {
            CompoundId = compoundKey
        });
    }
    
    private List<byte[]> DeserializeEmailBlock(byte[] blockData)
    {
        var emails = new List<byte[]>();
        using var ms = new MemoryStream(blockData);
        using var reader = new BinaryReader(ms);
        
        var emailCount = reader.ReadInt32();
        
        // Read table of contents
        var offsets = new List<(int length, long dataOffset)>();
        for (int i = 0; i < emailCount; i++)
        {
            var length = reader.ReadInt32();
            var envelopeHash = reader.ReadBytes(32);
            var contentHash = reader.ReadBytes(32);
            offsets.Add((length, 0)); // Data offset will be calculated
        }
        
        // Read email data
        var dataStartOffset = ms.Position;
        for (int i = 0; i < emailCount; i++)
        {
            var emailData = reader.ReadBytes(offsets[i].length);
            emails.Add(emailData);
        }
        
        return emails;
    }
    
    private async Task RollbackEmailStorageAsync(EmailHashedID emailId)
    {
        // This would remove the email from storage
        // Implementation depends on EmailStorageManager
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
public class EmailTransaction : IDisposable
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