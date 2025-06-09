using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tenray.ZoneTree;
using Tenray.ZoneTree.Serializers;
using MimeKit;
using EmailDB.Format.Models;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.ZoneTree;

namespace EmailDB.Format.Indexing;

/// <summary>
/// Manages all ZoneTree indexes for the EmailDB system.
/// </summary>
public class IndexManager : IDisposable
{
    // Primary indexes - for lookups
    private IZoneTree<string, string> _messageIdIndex;
    private IZoneTree<string, string> _envelopeHashIndex;
    private IZoneTree<string, string> _contentHashIndex;
    private IZoneTree<string, long> _folderPathIndex;
    
    // Secondary indexes - for efficiency
    private IZoneTree<string, BlockLocation> _emailLocationIndex;
    private IZoneTree<string, long> _envelopeLocationIndex;
    private IZoneTree<string, List<string>> _searchTermIndex;
    
    // Metadata
    private IZoneTree<string, IndexMetadata> _indexMetadataStore;
    
    private readonly string _indexDirectory;
    private bool _disposed;
    
    public IndexManager(string indexDirectory)
    {
        _indexDirectory = indexDirectory;
        
        Directory.CreateDirectory(indexDirectory);
        
        InitializeIndexes();
    }
    
    private void InitializeIndexes()
    {
        // Primary indexes
        _messageIdIndex = CreateIndex<string, string>("message_id", 
            new Utf8StringSerializer(), new Utf8StringSerializer());
            
        _envelopeHashIndex = CreateIndex<string, string>("envelope_hash",
            new Utf8StringSerializer(), new Utf8StringSerializer());
            
        _contentHashIndex = CreateIndex<string, string>("content_hash",
            new Utf8StringSerializer(), new Utf8StringSerializer());
            
        _folderPathIndex = CreateIndex<string, long>("folder_path",
            new Utf8StringSerializer(), new Int64Serializer());
        
        // Secondary indexes
        _emailLocationIndex = CreateIndex<string, BlockLocation>("email_location",
            new Utf8StringSerializer(), new BlockLocationSerializer());
            
        _envelopeLocationIndex = CreateIndex<string, long>("envelope_location",
            new Utf8StringSerializer(), new Int64Serializer());
            
        _searchTermIndex = CreateIndex<string, List<string>>("search_terms",
            new Utf8StringSerializer(), new StringListSerializer());
            
        _indexMetadataStore = CreateIndex<string, IndexMetadata>("index_metadata",
            new Utf8StringSerializer(), new IndexMetadataSerializer());
    }
    
    private IZoneTree<TKey, TValue> CreateIndex<TKey, TValue>(
        string name,
        ISerializer<TKey> keySerializer,
        ISerializer<TValue> valueSerializer)
    {
        var factory = new ZoneTreeFactory<TKey, TValue>()
            .SetDataDirectory(Path.Combine(_indexDirectory, name))
            .SetKeySerializer(keySerializer)
            .SetValueSerializer(valueSerializer);
            
        return factory.OpenOrCreate();
    }
    
    /// <summary>
    /// Indexes an email with all necessary lookups.
    /// </summary>
    public async Task<Result> IndexEmailAsync(
        EmailHashedID emailId,
        MimeMessage message,
        string folderPath,
        long envelopeBlockId)
    {
        try
        {
            var compoundKey = emailId.ToCompoundKey();
            
            // Primary indexes
            _messageIdIndex.Upsert(message.MessageId, compoundKey);
            _envelopeHashIndex.Upsert(
                Convert.ToBase64String(emailId.EnvelopeHash), 
                compoundKey);
            _contentHashIndex.Upsert(
                Convert.ToBase64String(emailId.ContentHash), 
                compoundKey);
            
            // Email location
            _emailLocationIndex.Upsert(compoundKey, new BlockLocation
            {
                BlockId = emailId.BlockId,
                LocalId = emailId.LocalId
            });
            
            // Envelope location for this email
            _envelopeLocationIndex.Upsert(compoundKey, envelopeBlockId);
            
            // Update search index
            await UpdateSearchIndexAsync(compoundKey, message);
            
            // Update index metadata
            await UpdateIndexMetadataAsync();
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Indexing failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Updates folder index when folder structure changes.
    /// </summary>
    public async Task<Result> UpdateFolderIndexAsync(string folderPath, long folderBlockId)
    {
        try
        {
            _folderPathIndex.Upsert(folderPath, folderBlockId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update folder index: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Looks up email by message ID.
    /// </summary>
    public Result<string> GetEmailByMessageId(string messageId)
    {
        if (_messageIdIndex.TryGet(messageId, out var compoundKey))
        {
            return Result<string>.Success(compoundKey);
        }
        return Result<string>.Failure("Email not found");
    }
    
    /// <summary>
    /// Checks for duplicate by envelope hash.
    /// </summary>
    public Result<string> GetEmailByEnvelopeHash(byte[] envelopeHash)
    {
        var hashKey = Convert.ToBase64String(envelopeHash);
        if (_envelopeHashIndex.TryGet(hashKey, out var compoundKey))
        {
            return Result<string>.Success(compoundKey);
        }
        return Result<string>.Failure("Email not found");
    }
    
    /// <summary>
    /// Gets block location for an email.
    /// </summary>
    public Result<BlockLocation> GetEmailLocation(string compoundKey)
    {
        if (_emailLocationIndex.TryGet(compoundKey, out var location))
        {
            return Result<BlockLocation>.Success(location);
        }
        return Result<BlockLocation>.Failure("Location not found");
    }
    
    /// <summary>
    /// Gets envelope block location for quick metadata access.
    /// </summary>
    public Result<long> GetEnvelopeBlockLocation(string compoundKey)
    {
        if (_envelopeLocationIndex.TryGet(compoundKey, out var blockId))
        {
            return Result<long>.Success(blockId);
        }
        return Result<long>.Failure("Envelope location not found");
    }
    
    /// <summary>
    /// Gets emails matching a search term.
    /// </summary>
    public Result<List<string>> GetEmailsBySearchTerm(string term)
    {
        if (_searchTermIndex.TryGet(term.ToLowerInvariant(), out var emailIds))
        {
            return Result<List<string>>.Success(emailIds);
        }
        return Result<List<string>>.Success(new List<string>());
    }
    
    private async Task UpdateSearchIndexAsync(string compoundKey, MimeMessage message)
    {
        var searchableText = new StringBuilder();
        searchableText.Append(message.Subject ?? "").Append(" ");
        searchableText.Append(message.TextBody ?? "").Append(" ");
        searchableText.Append(message.From?.ToString() ?? "").Append(" ");
        searchableText.Append(message.To?.ToString() ?? "");
        
        var words = ExtractSearchTerms(searchableText.ToString());
        
        foreach (var word in words)
        {
            if (_searchTermIndex.TryGet(word, out var existingList))
            {
                if (!existingList.Contains(compoundKey))
                {
                    existingList.Add(compoundKey);
                    _searchTermIndex.Upsert(word, existingList);
                }
            }
            else
            {
                _searchTermIndex.Upsert(word, new List<string> { compoundKey });
            }
        }
    }
    
    private HashSet<string> ExtractSearchTerms(string text)
    {
        // Simple word extraction - could be enhanced with better tokenization
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?' }, 
            StringSplitOptions.RemoveEmptyEntries);
            
        foreach (var token in tokens)
        {
            var cleaned = token.ToLowerInvariant().Trim();
            if (cleaned.Length >= 3) // Ignore very short words
            {
                words.Add(cleaned);
            }
        }
        
        return words;
    }
    
    private async Task UpdateIndexMetadataAsync()
    {
        var metadata = new IndexMetadata
        {
            LastUpdated = DateTime.UtcNow,
            TotalIndexedEmails = GetIndexedEmailCount(),
            IndexVersion = 1
        };
        
        _indexMetadataStore.Upsert("metadata", metadata);
    }
    
    private long GetIndexedEmailCount()
    {
        // This would need a more efficient implementation in production
        return _emailLocationIndex.Count();
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _messageIdIndex?.Dispose();
            _envelopeHashIndex?.Dispose();
            _contentHashIndex?.Dispose();
            _folderPathIndex?.Dispose();
            _emailLocationIndex?.Dispose();
            _envelopeLocationIndex?.Dispose();
            _searchTermIndex?.Dispose();
            _indexMetadataStore?.Dispose();
            _disposed = true;
        }
    }
}

// Supporting classes
public class BlockLocation
{
    public long BlockId { get; set; }
    public int LocalId { get; set; }
}

public class IndexMetadata
{
    public DateTime LastUpdated { get; set; }
    public long TotalIndexedEmails { get; set; }
    public int IndexVersion { get; set; }
}