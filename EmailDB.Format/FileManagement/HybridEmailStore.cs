using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Tenray.ZoneTree;
using EmailDB.Format.Models;
using EmailDB.Format.ZoneTree;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Hybrid email storage system:
/// - Append-only blocks for email data (efficient storage)
/// - ZoneTree for indexes (fast lookups and full-text search)
/// </summary>
public class HybridEmailStore : IDisposable
{
    private readonly AppendOnlyBlockStore _blockStore;
    private readonly string _indexDirectory;
    
    // ZoneTree indexes
    private readonly IZoneTree<string, string> _messageIdIndex; // MessageId -> EmailId
    private readonly IZoneTree<string, HashSet<string>> _folderIndex; // Folder -> Set<EmailId>
    private readonly IZoneTree<string, HashSet<string>> _fullTextIndex; // Word -> Set<EmailId>
    private readonly IZoneTree<string, EmailMetadata> _metadataIndex; // EmailId -> Metadata
    
    private bool _disposed;

    public HybridEmailStore(string dataPath, string indexDirectory, int blockSizeThreshold = 1024 * 1024)
    {
        _blockStore = new AppendOnlyBlockStore(dataPath, blockSizeThreshold);
        _indexDirectory = indexDirectory;
        Directory.CreateDirectory(indexDirectory);
        
        // Initialize ZoneTree indexes
        _messageIdIndex = new ZoneTreeFactory<string, string>()
            .SetDataDirectory(Path.Combine(indexDirectory, "message_id"))
            .SetIsDeletedDelegate((in string key, in string value) => string.IsNullOrEmpty(value))
            .OpenOrCreate();
            
        _folderIndex = new ZoneTreeFactory<string, HashSet<string>>()
            .SetDataDirectory(Path.Combine(indexDirectory, "folders"))
            .SetIsDeletedDelegate((in string key, in HashSet<string> value) => value == null || value.Count == 0)
            .OpenOrCreate();
            
        _fullTextIndex = new ZoneTreeFactory<string, HashSet<string>>()
            .SetDataDirectory(Path.Combine(indexDirectory, "fulltext"))
            .SetIsDeletedDelegate((in string key, in HashSet<string> value) => value == null || value.Count == 0)
            .OpenOrCreate();
            
        _metadataIndex = new ZoneTreeFactory<string, EmailMetadata>()
            .SetDataDirectory(Path.Combine(indexDirectory, "metadata"))
            .SetIsDeletedDelegate((in string key, in EmailMetadata value) => value == null)
            .OpenOrCreate();
    }

    /// <summary>
    /// Stores an email and updates all indexes.
    /// </summary>
    public async Task<EmailId> StoreEmailAsync(
        string messageId, 
        string folder, 
        byte[] emailData,
        string subject = null,
        string from = null,
        string to = null,
        string body = null,
        DateTime? date = null)
    {
        // Check for duplicates
        if (_messageIdIndex.TryGet(messageId, out _))
        {
            throw new InvalidOperationException($"Email with message ID {messageId} already exists");
        }
        
        // Store email data in append-only blocks
        var (blockId, localId) = await _blockStore.AppendEmailAsync(emailData);
        var emailId = new EmailId(blockId, localId);
        var emailIdStr = emailId.ToString();
        
        // Update message ID index
        _messageIdIndex.Upsert(messageId, emailIdStr);
        
        // Update folder index
        if (!_folderIndex.TryGet(folder, out var folderEmails))
        {
            folderEmails = new HashSet<string>();
        }
        folderEmails.Add(emailIdStr);
        _folderIndex.Upsert(folder, folderEmails);
        
        // Update full-text index
        if (!string.IsNullOrEmpty(body))
        {
            var words = ExtractWords(body);
            if (!string.IsNullOrEmpty(subject))
            {
                words.UnionWith(ExtractWords(subject));
            }
            
            foreach (var word in words)
            {
                if (!_fullTextIndex.TryGet(word, out var emailsWithWord))
                {
                    emailsWithWord = new HashSet<string>();
                }
                emailsWithWord.Add(emailIdStr);
                _fullTextIndex.Upsert(word, emailsWithWord);
            }
        }
        
        // Store metadata
        var metadata = new EmailMetadata
        {
            EmailId = emailId,
            MessageId = messageId,
            Folder = folder,
            Subject = subject,
            From = from,
            To = to,
            Date = date ?? DateTime.UtcNow,
            Size = emailData.Length,
            StoredAt = DateTime.UtcNow
        };
        _metadataIndex.Upsert(emailIdStr, metadata);
        
        return emailId;
    }

    /// <summary>
    /// Retrieves an email by its ID.
    /// </summary>
    public async Task<(byte[] data, EmailMetadata metadata)> GetEmailAsync(EmailId emailId)
    {
        var data = await _blockStore.ReadEmailAsync(emailId.BlockId, emailId.LocalId);
        
        if (_metadataIndex.TryGet(emailId.ToString(), out var metadata))
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
        if (!_messageIdIndex.TryGet(messageId, out var emailIdStr))
        {
            throw new KeyNotFoundException($"Email with message ID {messageId} not found");
        }
        
        var emailId = EmailId.Parse(emailIdStr);
        return await GetEmailAsync(emailId);
    }

    /// <summary>
    /// Lists all emails in a folder.
    /// </summary>
    public IEnumerable<EmailMetadata> ListFolder(string folder)
    {
        if (!_folderIndex.TryGet(folder, out var emailIds))
        {
            yield break;
        }
        
        foreach (var emailIdStr in emailIds)
        {
            if (_metadataIndex.TryGet(emailIdStr, out var metadata))
            {
                yield return metadata;
            }
        }
    }

    /// <summary>
    /// Searches emails by text content.
    /// </summary>
    public IEnumerable<EmailMetadata> SearchFullText(string query)
    {
        var words = ExtractWords(query);
        var resultSet = new HashSet<string>();
        bool first = true;
        
        foreach (var word in words)
        {
            if (_fullTextIndex.TryGet(word, out var emailIds))
            {
                if (first)
                {
                    resultSet.UnionWith(emailIds);
                    first = false;
                }
                else
                {
                    // Intersection for AND search
                    resultSet.IntersectWith(emailIds);
                }
            }
            else if (first)
            {
                // No results for first word
                yield break;
            }
        }
        
        foreach (var emailIdStr in resultSet)
        {
            if (_metadataIndex.TryGet(emailIdStr, out var metadata))
            {
                yield return metadata;
            }
        }
    }

    /// <summary>
    /// Moves an email to a different folder.
    /// </summary>
    public async Task MoveEmailAsync(EmailId emailId, string newFolder)
    {
        var emailIdStr = emailId.ToString();
        
        if (!_metadataIndex.TryGet(emailIdStr, out var metadata))
        {
            throw new KeyNotFoundException($"Email {emailId} not found");
        }
        
        var oldFolder = metadata.Folder;
        
        // Update folder index - remove from old folder
        if (_folderIndex.TryGet(oldFolder, out var oldFolderEmails))
        {
            oldFolderEmails.Remove(emailIdStr);
            if (oldFolderEmails.Count == 0)
            {
                _folderIndex.ForceDelete(oldFolder);
            }
            else
            {
                _folderIndex.Upsert(oldFolder, oldFolderEmails);
            }
        }
        
        // Add to new folder
        if (!_folderIndex.TryGet(newFolder, out var newFolderEmails))
        {
            newFolderEmails = new HashSet<string>();
        }
        newFolderEmails.Add(emailIdStr);
        _folderIndex.Upsert(newFolder, newFolderEmails);
        
        // Update metadata
        metadata.Folder = newFolder;
        _metadataIndex.Upsert(emailIdStr, metadata);
    }

    /// <summary>
    /// Deletes an email (marks as deleted in indexes, data remains in append-only store).
    /// </summary>
    public async Task DeleteEmailAsync(EmailId emailId)
    {
        var emailIdStr = emailId.ToString();
        
        if (!_metadataIndex.TryGet(emailIdStr, out var metadata))
        {
            throw new KeyNotFoundException($"Email {emailId} not found");
        }
        
        // Remove from message ID index
        _messageIdIndex.ForceDelete(metadata.MessageId);
        
        // Remove from folder index
        if (_folderIndex.TryGet(metadata.Folder, out var folderEmails))
        {
            folderEmails.Remove(emailIdStr);
            if (folderEmails.Count == 0)
            {
                _folderIndex.ForceDelete(metadata.Folder);
            }
            else
            {
                _folderIndex.Upsert(metadata.Folder, folderEmails);
            }
        }
        
        // Remove from full-text index (expensive, might want to optimize)
        // In production, might want to track which words are indexed per email
        
        // Remove metadata
        _metadataIndex.ForceDelete(emailIdStr);
    }

    /// <summary>
    /// Gets storage statistics.
    /// </summary>
    public StorageStats GetStats()
    {
        var emailCount = _metadataIndex.Count();
        var folderCount = _folderIndex.Count();
        var indexedWords = _fullTextIndex.Count();
        
        var dataFile = new FileInfo(_blockStore.GetType()
            .GetField("_filePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_blockStore)?.ToString() ?? "");
            
        var indexSize = GetDirectorySize(_indexDirectory);
        
        return new StorageStats
        {
            EmailCount = emailCount,
            FolderCount = folderCount,
            IndexedWords = indexedWords,
            DataFileSize = dataFile.Exists ? dataFile.Length : 0,
            IndexSize = indexSize,
            TotalSize = (dataFile.Exists ? dataFile.Length : 0) + indexSize
        };
    }

    private HashSet<string> ExtractWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new HashSet<string>();
            
        // Simple word extraction - in production use proper tokenizer
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}' }, 
            StringSplitOptions.RemoveEmptyEntries);
            
        foreach (var token in tokens)
        {
            var word = token.ToLowerInvariant().Trim();
            if (word.Length >= 3) // Skip very short words
            {
                words.Add(word);
            }
        }
        
        return words;
    }

    private long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;
            
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }

    public async Task FlushAsync()
    {
        await _blockStore.FlushAsync();
        // ZoneTree auto-persists
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _blockStore?.Dispose();
        _messageIdIndex?.Dispose();
        _folderIndex?.Dispose();
        _fullTextIndex?.Dispose();
        _metadataIndex?.Dispose();
        
        _disposed = true;
    }
}

public class StorageStats
{
    public long EmailCount { get; set; }
    public long FolderCount { get; set; }
    public long IndexedWords { get; set; }
    public long DataFileSize { get; set; }
    public long IndexSize { get; set; }
    public long TotalSize { get; set; }
}