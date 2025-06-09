using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Serializers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.ZoneTree;
using MimeKit;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Enhanced HybridEmailStore that delegates folder operations to FolderManager.
/// </summary>
public partial class HybridEmailStore
{
    private FolderManager _folderManager;
    private EmailStorageManager _storageManager;
    
    // Additional indexes for enhanced functionality
    private IZoneTree<string, string> _envelopeHashIndex; // EnvelopeHash -> CompoundKey
    private IZoneTree<string, string> _contentHashIndex; // ContentHash -> CompoundKey
    private IZoneTree<string, EmailLocation> _emailLocationIndex; // CompoundKey -> EmailLocation
    
    /// <summary>
    /// Enhanced constructor that uses FolderManager and EmailStorageManager.
    /// </summary>
    public HybridEmailStore(
        string dataPath, 
        string indexDirectory,
        FolderManager folderManager,
        EmailStorageManager storageManager,
        int blockSizeThreshold = 1024 * 1024) : this(dataPath, indexDirectory, blockSizeThreshold)
    {
        _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        
        // Initialize additional indexes
        _envelopeHashIndex = new ZoneTreeFactory<string, string>()
            .SetDataDirectory(Path.Combine(indexDirectory, "envelope_hash"))
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new Utf8StringSerializer())
            .OpenOrCreate();
            
        _contentHashIndex = new ZoneTreeFactory<string, string>()
            .SetDataDirectory(Path.Combine(indexDirectory, "content_hash"))
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new Utf8StringSerializer())
            .OpenOrCreate();
            
        _emailLocationIndex = new ZoneTreeFactory<string, EmailLocation>()
            .SetDataDirectory(Path.Combine(indexDirectory, "email_locations"))
            .SetKeySerializer(new Utf8StringSerializer())
            .SetValueSerializer(new EmailLocationSerializer())
            .OpenOrCreate();
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
            var compoundKey = $"{emailId.BlockId}:{emailId.LocalId}";
            
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
            _emailLocationIndex.Upsert(compoundKey, new EmailLocation
            {
                BlockId = emailId.BlockId,
                LocalId = emailId.LocalId
            });
            
            // Update full-text search index
            await UpdateFullTextIndexAsync(compoundKey, message);
            
            // Update metadata index
            var metadata = new EmailMetadata
            {
                EmailId = new EmailId(emailId.BlockId, emailId.LocalId),
                MessageId = message.MessageId,
                Folder = folderPath,
                Size = message.ToString().Length,
                StoredAt = DateTime.UtcNow,
                CustomMetadata = new Dictionary<string, object>
                {
                    { "Subject", message.Subject ?? "" },
                    { "From", message.From?.ToString() ?? "" },
                    { "To", message.To?.ToString() ?? "" },
                    { "Date", message.Date.ToString("O") }
                }
            };
            _metadataIndex.Upsert(compoundKey, metadata);
            
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
    public async Task<Result<EmailLocation>> GetEmailBlockLocationAsync(string compoundKey)
    {
        if (_emailLocationIndex.TryGet(compoundKey, out var location))
        {
            return Result<EmailLocation>.Success(location);
        }
        
        return Result<EmailLocation>.Failure("Email not found in index");
    }
    
    /// <summary>
    /// Gets email ID by message ID.
    /// </summary>
    public async Task<string> GetEmailIdByMessageIdAsync(string messageId)
    {
        if (_messageIdIndex.TryGet(messageId, out var compoundKey))
        {
            return compoundKey;
        }
        return null;
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
    
    /// <summary>
    /// Updates full-text index for an email.
    /// </summary>
    private async Task UpdateFullTextIndexAsync(string compoundKey, MimeMessage message)
    {
        var words = new HashSet<string>();
        
        // Extract words from subject
        if (!string.IsNullOrEmpty(message.Subject))
        {
            words.UnionWith(ExtractWords(message.Subject));
        }
        
        // Extract words from body
        var textBody = message.TextBody;
        if (!string.IsNullOrEmpty(textBody))
        {
            words.UnionWith(ExtractWords(textBody));
        }
        
        // Update full-text index
        foreach (var word in words)
        {
            if (!_fullTextIndex.TryGet(word, out var emailsWithWord))
            {
                emailsWithWord = new HashSet<string>();
            }
            else
            {
                // Create a copy to avoid concurrent modification
                emailsWithWord = new HashSet<string>(emailsWithWord);
            }
            emailsWithWord.Add(compoundKey);
            _fullTextIndex.Upsert(word, emailsWithWord);
        }
    }
    
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
/// Email location information.
/// </summary>
public class EmailLocation
{
    public long BlockId { get; set; }
    public int LocalId { get; set; }
}

/// <summary>
/// Serializer for EmailLocation.
/// </summary>
public class EmailLocationSerializer : ISerializer<EmailLocation>
{
    public EmailLocation Deserialize(Memory<byte> bytes)
    {
        using var ms = new MemoryStream(bytes.ToArray());
        using var reader = new BinaryReader(ms);
        return new EmailLocation
        {
            BlockId = reader.ReadInt64(),
            LocalId = reader.ReadInt32()
        };
    }
    
    public Memory<byte> Serialize(in EmailLocation value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(value.BlockId);
        writer.Write(value.LocalId);
        return ms.ToArray();
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
    
    public AtomicUpdateContext(HybridEmailStore store)
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
    
    public async Task CommitAsync()
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