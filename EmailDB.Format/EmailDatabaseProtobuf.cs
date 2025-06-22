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
using ProtoBuf;

namespace EmailDB.Format;

/// <summary>
/// High-level EmailDB API that uses Protobuf serialization for all data storage.
/// This ensures efficient binary serialization for ZoneTree data stored in blocks.
/// </summary>
public class EmailDatabaseProtobuf : IDisposable
{
    private readonly RawBlockManager _blockManager;
    private readonly IZoneTree<string, string> _emailStore;      // KV storage for emails (Protobuf as base64)
    private readonly IZoneTree<string, string> _searchIndex;     // Full-text search index (strings)
    private readonly IZoneTree<string, string> _folderIndex;     // Folder organization (Protobuf as base64)
    private readonly IZoneTree<string, string> _metadataStore;   // Email metadata (Protobuf as base64)
    private bool _disposed;

    public EmailDatabaseProtobuf(string databasePath)
    {
        _blockManager = new RawBlockManager(databasePath);
        
        // Get the directory path for ZoneTree data files
        var dataDirectory = Path.GetDirectoryName(databasePath);
        
        // Initialize KV storage for emails (Protobuf stored as base64 strings)
        var emailFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager, dataDirectory: dataDirectory);
        emailFactory.CreateZoneTree("emails");
        _emailStore = emailFactory.OpenOrCreate();

        // Initialize full-text search index (kept as string for efficiency)
        var searchFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager, dataDirectory: dataDirectory);
        searchFactory.CreateZoneTree("search");
        _searchIndex = searchFactory.OpenOrCreate();

        // Initialize folder index (Protobuf stored as base64 strings)
        var folderFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager, dataDirectory: dataDirectory);
        folderFactory.CreateZoneTree("folders");
        _folderIndex = folderFactory.OpenOrCreate();

        // Initialize metadata store (Protobuf stored as base64 strings)
        var metadataFactory = new EmailDBZoneTreeFactory<string, string>(_blockManager, dataDirectory: dataDirectory);
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
                    foundEmails[emailId.ToString()].RelevanceScore += 0.5f;
                }
            }
        }

        return foundEmails.Values.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
    }

    /// <summary>
    /// Get an email by its ID.
    /// </summary>
    public async Task<EmailContent> GetEmailAsync(EmailHashedID emailId)
    {
        var found = _emailStore.TryGet(emailId.ToString(), out var emailBase64);
        if (!found)
            throw new InvalidOperationException($"Email {emailId} not found");

        // Decode from base64 and deserialize from Protobuf
        var emailBytes = Convert.FromBase64String(emailBase64);
        using var stream = new MemoryStream(emailBytes);
        var protoEmail = Serializer.Deserialize<ProtoEmailContent>(stream);
        
        // Convert to EmailContent
        return new EmailContent
        {
            MessageId = protoEmail.MessageId,
            Subject = protoEmail.Subject,
            From = protoEmail.From,
            To = protoEmail.To,
            Date = protoEmail.Date,
            TextBody = protoEmail.TextBody,
            HtmlBody = protoEmail.HtmlBody,
            Size = protoEmail.Size,
            FileName = protoEmail.FileName
        };
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
        _folderIndex.TryAdd(folderKey, emailId.ToString(), out _); // Just store the ID string
        
        var emailFolderKey = $"email_folders:{emailId}";
        var existingFolders = new ProtoStringList();
        
        if (_folderIndex.TryGet(emailFolderKey, out var foldersBase64))
        {
            var foldersBytes = Convert.FromBase64String(foldersBase64);
            using var stream = new MemoryStream(foldersBytes);
            existingFolders = Serializer.Deserialize<ProtoStringList>(stream);
        }
        
        if (!existingFolders.Items.Contains(folderName))
        {
            existingFolders.Items.Add(folderName);
            _folderIndex.Upsert(emailFolderKey, SerializeProtoToBase64(existingFolders));
        }
    }

    /// <summary>
    /// Get folders/labels for an email.
    /// </summary>
    public async Task<List<string>> GetEmailFoldersAsync(EmailHashedID emailId)
    {
        var emailFolderKey = $"email_folders:{emailId}";
        var found = _folderIndex.TryGet(emailFolderKey, out var foldersBase64);
        
        if (!found) return new List<string>();
        
        var foldersBytes = Convert.FromBase64String(foldersBase64);
        using var stream = new MemoryStream(foldersBytes);
        var protoList = Serializer.Deserialize<ProtoStringList>(stream);
        return protoList.Items;
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
        var protoEmail = new ProtoEmailContent
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
        
        // Serialize to Protobuf and store as base64
        var emailBase64 = SerializeProtoToBase64(protoEmail);
        _emailStore.TryAdd(emailId.ToString(), emailBase64, out _);
        
        // Create full-text search indexes (keep as strings for search efficiency)
        await IndexEmailForSearchAsync(emailId, protoEmail);
        
        // Store metadata
        await StoreEmailMetadataAsync(emailId, protoEmail);
        
        return emailId;
    }

    private async Task IndexEmailForSearchAsync(EmailHashedID emailId, ProtoEmailContent email)
    {
        // Index fields as strings for efficient search
        if (!string.IsNullOrEmpty(email.Subject))
        {
            var subjectKey = $"subject:{emailId}";
            _searchIndex.TryAdd(subjectKey, email.Subject.ToLowerInvariant(), out _);
        }

        if (!string.IsNullOrEmpty(email.From))
        {
            var fromKey = $"from:{emailId}";
            _searchIndex.TryAdd(fromKey, email.From.ToLowerInvariant(), out _);
        }

        if (!string.IsNullOrEmpty(email.To))
        {
            var toKey = $"to:{emailId}";
            _searchIndex.TryAdd(toKey, email.To.ToLowerInvariant(), out _);
        }

        if (!string.IsNullOrEmpty(email.TextBody))
        {
            var bodyKey = $"body:{emailId}";
            _searchIndex.TryAdd(bodyKey, email.TextBody.ToLowerInvariant(), out _);
        }
    }

    private async Task StoreEmailMetadataAsync(EmailHashedID emailId, ProtoEmailContent email)
    {
        var metadata = new ProtoEmailMetadata
        {
            EmailId = emailId.ToString(),
            ImportDate = DateTime.UtcNow,
            FileName = email.FileName,
            Size = email.Size,
            HasAttachments = false // Simplified for demo
        };

        var metadataBase64 = SerializeProtoToBase64(metadata);
        _metadataStore.TryAdd($"metadata:{emailId}", metadataBase64, out _);
    }

    private async Task<List<EmailHashedID>> GetEmailIDsAsync()
    {
        var emailIds = new List<EmailHashedID>();
        
        // Get the email IDs index
        var metadataKey = "email_ids_index";
        if (_metadataStore.TryGet(metadataKey, out var indexBase64))
        {
            var indexBytes = Convert.FromBase64String(indexBase64);
            using var stream = new MemoryStream(indexBytes);
            var idList = Serializer.Deserialize<ProtoEmailIdList>(stream);
            emailIds.AddRange(idList.EmailIds.Select(id => EmailHashedID.FromBase32String(id)));
        }

        return emailIds;
    }

    public void UpdateEmailIdsIndex(List<EmailHashedID> emailIds)
    {
        var idList = new ProtoEmailIdList
        {
            EmailIds = emailIds.Select(id => id.ToString()).ToList()
        };
        
        var metadataKey = "email_ids_index";
        _metadataStore.Upsert(metadataKey, SerializeProtoToBase64(idList));
    }

    private async Task<int> CountSearchIndexesAsync()
    {
        var emailIds = await GetEmailIDsAsync();
        return emailIds.Count * 4; // subject, from, to, body per email
    }

    private async Task<int> CountFoldersAsync()
    {
        return 5; // Mock implementation
    }

    private string SerializeProtoToBase64<T>(T obj)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, obj);
        return Convert.ToBase64String(stream.ToArray());
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