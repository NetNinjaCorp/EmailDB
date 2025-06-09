using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using EmailDB.Format.Models;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// High-level email storage using append-only blocks with in-memory indexing.
/// </summary>
public class AppendOnlyEmailStore : IDisposable
{
    private readonly AppendOnlyBlockStore _blockStore;
    private readonly string _indexPath;
    private readonly ConcurrentDictionary<string, EmailId> _messageIdIndex = new();
    private readonly ConcurrentDictionary<string, HashSet<EmailId>> _folderIndex = new();
    private readonly ConcurrentDictionary<EmailId, EmailMetadata> _metadataCache = new();
    
    public AppendOnlyEmailStore(string dataPath, string indexPath, int blockSizeThreshold = 1024 * 1024)
    {
        _blockStore = new AppendOnlyBlockStore(dataPath, blockSizeThreshold);
        _indexPath = indexPath;
        
        // Load indexes if they exist
        if (File.Exists(indexPath))
        {
            LoadIndexes();
        }
    }

    /// <summary>
    /// Stores an email and returns its unique ID.
    /// </summary>
    public async Task<EmailId> StoreEmailAsync(string messageId, string folder, byte[] emailData, Dictionary<string, object> metadata = null)
    {
        // Check for duplicates
        if (_messageIdIndex.ContainsKey(messageId))
        {
            throw new InvalidOperationException($"Email with message ID {messageId} already exists");
        }
        
        // Store the email data
        var (blockId, localId) = await _blockStore.AppendEmailAsync(emailData);
        var emailId = new EmailId(blockId, localId);
        
        // Update indexes
        _messageIdIndex[messageId] = emailId;
        
        var folderEmails = _folderIndex.GetOrAdd(folder, _ => new HashSet<EmailId>());
        lock (folderEmails)
        {
            folderEmails.Add(emailId);
        }
        
        // Cache metadata
        var emailMetadata = new EmailMetadata
        {
            EmailId = emailId,
            MessageId = messageId,
            Folder = folder,
            Size = emailData.Length,
            StoredAt = DateTime.UtcNow,
            CustomMetadata = metadata
        };
        _metadataCache[emailId] = emailMetadata;
        
        return emailId;
    }

    /// <summary>
    /// Retrieves an email by its unique ID.
    /// </summary>
    public async Task<(byte[] data, EmailMetadata metadata)> GetEmailAsync(EmailId emailId)
    {
        var data = await _blockStore.ReadEmailAsync(emailId.BlockId, emailId.LocalId);
        
        if (_metadataCache.TryGetValue(emailId, out var metadata))
        {
            return (data, metadata);
        }
        
        return (data, null);
    }

    /// <summary>
    /// Retrieves an email by message ID.
    /// </summary>
    public async Task<(byte[] data, EmailMetadata metadata)> GetEmailByMessageIdAsync(string messageId)
    {
        if (!_messageIdIndex.TryGetValue(messageId, out var emailId))
        {
            throw new KeyNotFoundException($"Email with message ID {messageId} not found");
        }
        
        return await GetEmailAsync(emailId);
    }

    /// <summary>
    /// Lists all emails in a folder.
    /// </summary>
    public IEnumerable<EmailMetadata> ListFolder(string folder)
    {
        if (!_folderIndex.TryGetValue(folder, out var emailIds))
        {
            yield break;
        }
        
        lock (emailIds)
        {
            foreach (var emailId in emailIds)
            {
                if (_metadataCache.TryGetValue(emailId, out var metadata))
                {
                    yield return metadata;
                }
            }
        }
    }

    /// <summary>
    /// Moves an email to a different folder (creates a new version).
    /// </summary>
    public async Task<EmailId> MoveEmailAsync(EmailId oldId, string newFolder)
    {
        // Read the old email
        var (data, metadata) = await GetEmailAsync(oldId);
        if (metadata == null)
        {
            throw new InvalidOperationException($"Metadata for email {oldId} not found");
        }
        
        // Store as new version in new folder
        var newId = await StoreEmailAsync(
            metadata.MessageId + $"_moved_{DateTime.UtcNow.Ticks}",
            newFolder,
            data,
            metadata.CustomMetadata
        );
        
        // Mark old version as moved
        metadata.MovedTo = newId;
        metadata.MovedAt = DateTime.UtcNow;
        
        return newId;
    }

    /// <summary>
    /// Flushes pending data and saves indexes.
    /// </summary>
    public async Task FlushAsync()
    {
        await _blockStore.FlushAsync();
        await SaveIndexesAsync();
    }

    private void LoadIndexes()
    {
        var json = File.ReadAllText(_indexPath);
        var data = JsonSerializer.Deserialize<IndexData>(json);
        
        foreach (var (messageId, emailIdStr) in data.MessageIdIndex)
        {
            _messageIdIndex[messageId] = EmailId.Parse(emailIdStr);
        }
        
        foreach (var (folder, emailIdStrs) in data.FolderIndex)
        {
            var emailIds = new HashSet<EmailId>();
            foreach (var idStr in emailIdStrs)
            {
                emailIds.Add(EmailId.Parse(idStr));
            }
            _folderIndex[folder] = emailIds;
        }
        
        foreach (var metadata in data.MetadataCache)
        {
            _metadataCache[metadata.EmailId] = metadata;
        }
    }

    private async Task SaveIndexesAsync()
    {
        var data = new IndexData
        {
            MessageIdIndex = new Dictionary<string, string>(),
            FolderIndex = new Dictionary<string, List<string>>(),
            MetadataCache = new List<EmailMetadata>()
        };
        
        foreach (var (messageId, emailId) in _messageIdIndex)
        {
            data.MessageIdIndex[messageId] = emailId.ToString();
        }
        
        foreach (var (folder, emailIds) in _folderIndex)
        {
            var idStrs = new List<string>();
            lock (emailIds)
            {
                foreach (var id in emailIds)
                {
                    idStrs.Add(id.ToString());
                }
            }
            data.FolderIndex[folder] = idStrs;
        }
        
        foreach (var metadata in _metadataCache.Values)
        {
            data.MetadataCache.Add(metadata);
        }
        
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_indexPath, json);
    }

    public void Dispose()
    {
        FlushAsync().GetAwaiter().GetResult();
        _blockStore?.Dispose();
    }

    private class IndexData
    {
        public Dictionary<string, string> MessageIdIndex { get; set; }
        public Dictionary<string, List<string>> FolderIndex { get; set; }
        public List<EmailMetadata> MetadataCache { get; set; }
    }
}

public class EmailMetadata
{
    public EmailId EmailId { get; set; }
    public string MessageId { get; set; }
    public string Folder { get; set; }
    public int Size { get; set; }
    public DateTime StoredAt { get; set; }
    public Dictionary<string, object> CustomMetadata { get; set; }
    
    // For tracking moves/updates
    public EmailId? MovedTo { get; set; }
    public DateTime? MovedAt { get; set; }
}