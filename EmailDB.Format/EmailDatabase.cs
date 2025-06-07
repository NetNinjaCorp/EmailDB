using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.ZoneTree;
using EmailDB.Format.Models;
using MimeKit;
using Tenray.ZoneTree;

namespace EmailDB.Format;

/// <summary>
/// High-level EmailDB API that abstracts away ZoneTree complexity.
/// Provides simple methods for EML processing, storage, and full-text search
/// using custom EmailDB block storage format.
/// </summary>
public class EmailDatabase : IDisposable
{
    private readonly RawBlockManager _blockManager;
    private readonly IZoneTree<string, string> _emailStore;      // KV storage for emails
    private readonly IZoneTree<string, string> _searchIndex;     // Full-text search index
    private readonly IZoneTree<string, string> _folderIndex;     // Folder/label organization
    private readonly IZoneTree<string, string> _metadataStore;   // Email metadata
    private bool _disposed;

    public EmailDatabase(string databasePath)
    {
        _blockManager = new RawBlockManager(databasePath);
        
        // Initialize KV storage for emails
        var emailFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager);
        emailFactory.CreateZoneTree("emails");
        _emailStore = emailFactory.OpenOrCreate();

        // Initialize full-text search index
        var searchFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager);
        searchFactory.CreateZoneTree("search");
        _searchIndex = searchFactory.OpenOrCreate();

        // Initialize folder index
        var folderFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager);
        folderFactory.CreateZoneTree("folders");
        _folderIndex = folderFactory.OpenOrCreate();

        // Initialize metadata store
        var metadataFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager);
        metadataFactory.CreateZoneTree("metadata");
        _metadataStore = metadataFactory.OpenOrCreate();
    }

    /// <summary>
    /// Import an EML file into the database with full-text indexing.
    /// </summary>
    public async Task<EmailHashedID> ImportEMLAsync(string emlContent, string fileName = null)
    {
        var message = MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(emlContent)));
        return await ImportMessageAsync(message, fileName);
    }

    /// <summary>
    /// Import an EML file from disk.
    /// </summary>
    public async Task<EmailHashedID> ImportEMLFileAsync(string filePath)
    {
        var emlContent = await File.ReadAllTextAsync(filePath);
        var fileName = Path.GetFileName(filePath);
        return await ImportEMLAsync(emlContent, fileName);
    }

    /// <summary>
    /// Import multiple EML files in a batch operation.
    /// </summary>
    public async Task<BatchImportResult> ImportEMLBatchAsync((string fileName, string emlContent)[] emails)
    {
        var result = new BatchImportResult();
        
        foreach (var (fileName, emlContent) in emails)
        {
            try
            {
                var emailId = await ImportEMLAsync(emlContent, fileName);
                result.SuccessCount++;
                result.ImportedEmailIds.Add(emailId);
            }
            catch (Exception ex)
            {
                result.ErrorCount++;
                result.Errors.Add($"{fileName}: {ex.Message}");
            }
        }
        
        return result;
    }

    /// <summary>
    /// Search emails using full-text search.
    /// </summary>
    public async Task<List<EmailSearchResult>> SearchAsync(string searchTerm, int maxResults = 50)
    {
        var results = new List<EmailSearchResult>();
        var searchTermLower = searchTerm.ToLowerInvariant();

        // Search through all indexed content
        var searchKeys = new[] { "subject", "from", "to", "body" };
        var foundEmails = new Dictionary<string, EmailSearchResult>();

        foreach (var field in searchKeys)
        {
            // Search for emails containing the term in this field
            var emailIds = await GetEmailIDsAsync();
            
            foreach (var emailId in emailIds)
            {
                var indexKey = $"{field}:{emailId}";
                var found = _searchIndex.TryGet(indexKey, out var indexedContent);
                
                if (found && indexedContent.Contains(searchTermLower))
                {
                    if (!foundEmails.ContainsKey(emailId.ToString()))
                    {
                        var email = await GetEmailAsync(emailId);
                        foundEmails[emailId.ToString()] = new EmailSearchResult
                        {
                            EmailId = emailId,
                            Subject = email.Subject,
                            From = email.From?.ToString() ?? "",
                            Date = email.Date,
                            RelevanceScore = 1.0f,
                            MatchedFields = new List<string>()
                        };
                    }
                    
                    foundEmails[emailId.ToString()].MatchedFields.Add(field);
                    foundEmails[emailId.ToString()].RelevanceScore += 0.5f; // Boost score for multiple matches
                }
            }
        }

        return foundEmails.Values.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
    }

    /// <summary>
    /// Advanced search with query syntax (basic implementation).
    /// </summary>
    public async Task<List<EmailSearchResult>> AdvancedSearchAsync(string query)
    {
        // Simple implementation - can be enhanced with proper query parsing
        var terms = query.ToLowerInvariant().Split(new[] { " and ", " or ", " not " }, StringSplitOptions.RemoveEmptyEntries);
        var firstTerm = terms.FirstOrDefault()?.Trim() ?? query;
        
        return await SearchAsync(firstTerm);
    }

    /// <summary>
    /// Get an email by its ID.
    /// </summary>
    public async Task<EmailContent> GetEmailAsync(EmailHashedID emailId)
    {
        var found = _emailStore.TryGet(emailId.ToString(), out var emailJson);
        if (!found)
            throw new InvalidOperationException($"Email {emailId} not found");

        var emailData = System.Text.Json.JsonSerializer.Deserialize<EmailContent>(emailJson);
        return emailData ?? throw new InvalidDataException("Failed to deserialize email data");
    }

    /// <summary>
    /// Get all email IDs in the database.
    /// </summary>
    public async Task<List<EmailHashedID>> GetAllEmailIDsAsync()
    {
        return await GetEmailIDsAsync();
    }

    /// <summary>
    /// Add an email to a folder/label.
    /// </summary>
    public async Task AddToFolderAsync(EmailHashedID emailId, string folderName)
    {
        var folderKey = $"folder:{folderName}:{emailId}";
        _folderIndex.TryAdd(folderKey, emailId.ToString(), out _);
        
        var emailFolderKey = $"email_folders:{emailId}";
        var existingFolders = _folderIndex.TryGet(emailFolderKey, out var foldersJson) 
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(foldersJson) ?? new List<string>()
            : new List<string>();
        
        if (!existingFolders.Contains(folderName))
        {
            existingFolders.Add(folderName);
            var updatedJson = System.Text.Json.JsonSerializer.Serialize(existingFolders);
            _folderIndex.TryAdd(emailFolderKey, updatedJson, out _);
        }
    }

    /// <summary>
    /// Get folders/labels for an email.
    /// </summary>
    public async Task<List<string>> GetEmailFoldersAsync(EmailHashedID emailId)
    {
        var emailFolderKey = $"email_folders:{emailId}";
        var found = _folderIndex.TryGet(emailFolderKey, out var foldersJson);
        
        if (!found) return new List<string>();
        
        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(foldersJson) ?? new List<string>();
    }

    /// <summary>
    /// Get indexed fields for an email.
    /// </summary>
    public async Task<List<string>> GetIndexedFieldsAsync(EmailHashedID emailId)
    {
        var fields = new List<string>();
        var indexFields = new[] { "subject", "from", "to", "body" };
        
        foreach (var field in indexFields)
        {
            var indexKey = $"{field}:{emailId}";
            if (_searchIndex.TryGet(indexKey, out _))
            {
                fields.Add(field);
            }
        }
        
        return fields;
    }

    /// <summary>
    /// Get database statistics.
    /// </summary>
    public async Task<DatabaseStats> GetDatabaseStatsAsync()
    {
        var emailIds = await GetEmailIDsAsync();
        var blocks = _blockManager.GetBlockLocations();
        
        return new DatabaseStats
        {
            TotalEmails = emailIds.Count,
            StorageBlocks = blocks.Count,
            SearchIndexes = await CountSearchIndexesAsync(),
            TotalFolders = await CountFoldersAsync()
        };
    }

    private async Task<EmailHashedID> ImportMessageAsync(MimeMessage message, string fileName)
    {
        // Create email content
        var emailContent = new EmailContent
        {
            MessageId = message.MessageId ?? Guid.NewGuid().ToString(),
            Subject = message.Subject ?? "",
            From = message.From?.FirstOrDefault()?.ToString() ?? "",
            To = string.Join("; ", message.To?.Select(addr => addr.ToString()) ?? new string[0]),
            Date = message.Date.DateTime,
            TextBody = message.TextBody ?? "",
            HtmlBody = message.HtmlBody ?? "",
            Size = System.Text.Encoding.UTF8.GetByteCount(message.ToString()),
            FileName = fileName
        };

        // Generate email ID
        var emailId = new EmailHashedID(message);
        
        // Store email in KV store
        var emailJson = System.Text.Json.JsonSerializer.Serialize(emailContent);
        _emailStore.TryAdd(emailId.ToString(), emailJson, out _);
        
        // Create full-text search indexes
        await IndexEmailForSearchAsync(emailId, emailContent);
        
        // Store metadata
        await StoreEmailMetadataAsync(emailId, emailContent);
        
        return emailId;
    }

    private async Task IndexEmailForSearchAsync(EmailHashedID emailId, EmailContent email)
    {
        // Index subject
        if (!string.IsNullOrEmpty(email.Subject))
        {
            var subjectKey = $"subject:{emailId}";
            _searchIndex.TryAdd(subjectKey, email.Subject.ToLowerInvariant(), out _);
        }

        // Index from
        if (!string.IsNullOrEmpty(email.From))
        {
            var fromKey = $"from:{emailId}";
            _searchIndex.TryAdd(fromKey, email.From.ToLowerInvariant(), out _);
        }

        // Index to
        if (!string.IsNullOrEmpty(email.To))
        {
            var toKey = $"to:{emailId}";
            _searchIndex.TryAdd(toKey, email.To.ToLowerInvariant(), out _);
        }

        // Index body
        if (!string.IsNullOrEmpty(email.TextBody))
        {
            var bodyKey = $"body:{emailId}";
            _searchIndex.TryAdd(bodyKey, email.TextBody.ToLowerInvariant(), out _);
        }
    }

    private async Task StoreEmailMetadataAsync(EmailHashedID emailId, EmailContent email)
    {
        var metadata = new EmailMetadata
        {
            EmailId = emailId,
            ImportDate = DateTime.UtcNow,
            FileName = email.FileName,
            Size = email.Size,
            HasAttachments = false // Simplified for demo
        };

        var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
        _metadataStore.TryAdd($"metadata:{emailId}", metadataJson, out _);
    }

    private async Task<List<EmailHashedID>> GetEmailIDsAsync()
    {
        var emailIds = new List<EmailHashedID>();
        
        // This is a simplified implementation - in practice you'd want a more efficient index
        var metadataKey = "email_ids_index";
        if (_metadataStore.TryGet(metadataKey, out var indexJson))
        {
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(indexJson) ?? new List<string>();
            emailIds.AddRange(ids.Select(id => EmailHashedID.FromBase32String(id)));
        }

        return emailIds;
    }

    private async Task<int> CountSearchIndexesAsync()
    {
        // Simplified count - count search index entries
        var emailIds = await GetEmailIDsAsync();
        return emailIds.Count * 4; // subject, from, to, body per email
    }

    private async Task<int> CountFoldersAsync()
    {
        // Simplified folder count
        return 5; // Mock implementation
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _emailStore?.Dispose();
            _searchIndex?.Dispose();
            _folderIndex?.Dispose();
            _metadataStore?.Dispose();
            _blockManager?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Result of a batch import operation.
/// </summary>
public class BatchImportResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<EmailHashedID> ImportedEmailIds { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Email search result.
/// </summary>
public class EmailSearchResult
{
    public EmailHashedID EmailId { get; set; }
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public DateTime Date { get; set; }
    public float RelevanceScore { get; set; }
    public List<string> MatchedFields { get; set; } = new();
}

/// <summary>
/// Database statistics.
/// </summary>
public class DatabaseStats
{
    public int TotalEmails { get; set; }
    public int StorageBlocks { get; set; }
    public int SearchIndexes { get; set; }
    public int TotalFolders { get; set; }
}

/// <summary>
/// Simplified email content for demonstration.
/// </summary>
public class EmailContent
{
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTime Date { get; set; }
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public long Size { get; set; }
    public string? FileName { get; set; }
}

/// <summary>
/// Email metadata.
/// </summary>
public class EmailMetadata
{
    public EmailHashedID EmailId { get; set; }
    public DateTime ImportDate { get; set; }
    public string? FileName { get; set; }
    public long Size { get; set; }
    public bool HasAttachments { get; set; }
}