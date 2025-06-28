# Phase 3 Implementation Plan: Index Management ✅ COMPLETED

## Overview
Phase 3 focuses on optimizing the index management system to work with the new block-based architecture. All ZoneTree indexes will be refactored to store only block references (not actual data), and search functionality will be optimized to use envelope blocks for efficient previews.

**Status: COMPLETED** - All index management optimizations have been successfully implemented.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        Index Architecture                        │
├─────────────────────────────────────────────────────────────────┤
│ Primary Indexes (ZoneTree)                                       │
│ ├── MessageId → CompoundKey (BlockId:LocalId)                   │
│ ├── EnvelopeHash → CompoundKey (deduplication)                  │
│ ├── ContentHash → CompoundKey (verification)                    │
│ └── FolderPath → FolderBlockLocation                           │
│                                                                  │
│ Secondary Indexes (ZoneTree)                                     │
│ ├── CompoundKey → EnvelopeBlockLocation (metadata)              │
│ ├── FolderPath → EnvelopeBlockLocation (listings)              │
│ └── SearchTerm → List<CompoundKey> (full-text)                 │
│                                                                  │
│ Index Services                                                   │
│ ├── IndexManager (coordinates all indexes)                       │
│ ├── SearchOptimizer (efficient search with previews)            │
│ └── IndexRebuilder (recovery and maintenance)                   │
└─────────────────────────────────────────────────────────────────┘
```

## Section 3.1: ZoneTree Index Refactoring

### Current State Analysis
Existing indexes store actual data:
- MessageId → EmailId (string)
- Folder → Set<EmailId> (HashSet)
- Word → Set<EmailId> (for search)
- EmailId → Metadata (full object)

### Target State
All indexes store only references:
- MessageId → CompoundKey (BlockId:LocalId)
- EnvelopeHash → CompoundKey
- FolderPath → BlockLocation
- SearchTerm → List<CompoundKey>

### Task 3.1.1: Create Index Manager
**File**: `EmailDB.Format/Indexing/IndexManager.cs`
**Dependencies**: ZoneTree, BlockLocation types
**Description**: Centralized manager for all index operations

```csharp
namespace EmailDB.Format.Indexing;

/// <summary>
/// Manages all ZoneTree indexes for the EmailDB system.
/// </summary>
public class IndexManager : IDisposable
{
    // Primary indexes - for lookups
    private readonly IZoneTree<string, string> _messageIdIndex;
    private readonly IZoneTree<string, string> _envelopeHashIndex;
    private readonly IZoneTree<string, string> _contentHashIndex;
    private readonly IZoneTree<string, long> _folderPathIndex;
    
    // Secondary indexes - for efficiency
    private readonly IZoneTree<string, BlockLocation> _emailLocationIndex;
    private readonly IZoneTree<string, long> _envelopeLocationIndex;
    private readonly IZoneTree<string, List<string>> _searchTermIndex;
    
    // Metadata
    private readonly IZoneTree<string, IndexMetadata> _indexMetadataStore;
    
    private readonly string _indexDirectory;
    private readonly ILogger _logger;
    private bool _disposed;
    
    public IndexManager(string indexDirectory, ILogger logger = null)
    {
        _indexDirectory = indexDirectory;
        _logger = logger ?? new ConsoleLogger();
        
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
            .SetValueSerializer(valueSerializer)
            .SetLogger(_logger);
            
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
            _logger.LogError($"Failed to index email: {ex.Message}");
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
```

### Task 3.1.2: Create Custom Serializers
**File**: `EmailDB.Format/Indexing/Serializers.cs`
**Dependencies**: Tenray.ZoneTree.Serializers
**Description**: Custom serializers for index data types

```csharp
namespace EmailDB.Format.Indexing;

/// <summary>
/// Serializer for BlockLocation objects.
/// </summary>
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

/// <summary>
/// Serializer for List<string> used in search indexes.
/// </summary>
public class StringListSerializer : ISerializer<List<string>>
{
    public List<string> Deserialize(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);
        
        var count = reader.ReadInt32();
        var list = new List<string>(count);
        
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadString());
        }
        
        return list;
    }
    
    public byte[] Serialize(in List<string> value)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(value.Count);
        foreach (var item in value)
        {
            writer.Write(item);
        }
        
        return ms.ToArray();
    }
}

/// <summary>
/// Serializer for IndexMetadata.
/// </summary>
public class IndexMetadataSerializer : ISerializer<IndexMetadata>
{
    public IndexMetadata Deserialize(byte[] bytes)
    {
        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize<IndexMetadata>(json);
    }
    
    public byte[] Serialize(in IndexMetadata value)
    {
        var json = JsonSerializer.Serialize(value);
        return Encoding.UTF8.GetBytes(json);
    }
}
```

### Task 3.1.3: Update HybridEmailStore to Use IndexManager
**File**: `EmailDB.Format/FileManagement/HybridEmailStore.cs` (modifications)
**Description**: Refactor to use centralized IndexManager

```csharp
public partial class HybridEmailStore
{
    private readonly IndexManager _indexManager;
    
    // Remove individual index declarations and replace with IndexManager
    
    public HybridEmailStore(
        string dataPath, 
        string indexDirectory,
        FolderManager folderManager,
        EmailStorageManager storageManager,
        IndexManager indexManager,
        int blockSizeThreshold = 1024 * 1024)
    {
        _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        
        _indexDirectory = indexDirectory;
        Directory.CreateDirectory(indexDirectory);
    }
    
    /// <summary>
    /// Updates indexes for a newly stored email.
    /// </summary>
    public async Task<Result> UpdateIndexesForEmailAsync(
        EmailHashedID emailId,
        MimeMessage message,
        string folderPath,
        long envelopeBlockId)
    {
        return await _indexManager.IndexEmailAsync(emailId, message, folderPath, envelopeBlockId);
    }
    
    /// <summary>
    /// Gets the block location for an email.
    /// </summary>
    public async Task<Result<BlockLocation>> GetEmailBlockLocationAsync(string compoundKey)
    {
        return _indexManager.GetEmailLocation(compoundKey);
    }
    
    /// <summary>
    /// Checks for duplicate emails by envelope hash.
    /// </summary>
    public async Task<Result<string>> CheckDuplicateAsync(byte[] envelopeHash)
    {
        return _indexManager.GetEmailByEnvelopeHash(envelopeHash);
    }
}
```

### Task 3.1.4: Implement Index Rebuilding
**File**: `EmailDB.Format/Indexing/IndexRebuilder.cs`
**Dependencies**: RawBlockManager, IndexManager
**Description**: Rebuilds indexes by scanning all blocks

```csharp
namespace EmailDB.Format.Indexing;

/// <summary>
/// Rebuilds indexes by scanning all blocks in the database.
/// </summary>
public class IndexRebuilder
{
    private readonly RawBlockManager _blockManager;
    private readonly IndexManager _indexManager;
    private readonly IBlockContentSerializer _serializer;
    private readonly ILogger _logger;
    
    public IndexRebuilder(
        RawBlockManager blockManager,
        IndexManager indexManager,
        IBlockContentSerializer serializer,
        ILogger logger = null)
    {
        _blockManager = blockManager;
        _indexManager = indexManager;
        _serializer = serializer;
        _logger = logger ?? new ConsoleLogger();
    }
    
    /// <summary>
    /// Rebuilds all indexes by scanning blocks.
    /// </summary>
    public async Task<Result> RebuildAllIndexesAsync(IProgress<RebuildProgress> progress = null)
    {
        try
        {
            _logger.LogInfo("Starting index rebuild...");
            
            var blockLocations = _blockManager.GetBlockLocations();
            var totalBlocks = blockLocations.Count;
            var processedBlocks = 0;
            
            var rebuildProgress = new RebuildProgress
            {
                TotalBlocks = totalBlocks,
                Phase = "Scanning blocks"
            };
            
            // Process each block type
            foreach (var (offset, location) in blockLocations)
            {
                var blockResult = await _blockManager.ReadBlockAsync(offset);
                if (!blockResult.IsSuccess)
                {
                    _logger.LogWarning($"Failed to read block at offset {offset}");
                    continue;
                }
                
                var block = blockResult.Value;
                
                switch (block.Type)
                {
                    case BlockType.EmailBatch:
                        await ProcessEmailBatchBlock(block, offset);
                        break;
                        
                    case BlockType.Folder:
                        await ProcessFolderBlock(block, offset);
                        break;
                        
                    case BlockType.FolderEnvelope:
                        await ProcessEnvelopeBlock(block, offset);
                        break;
                }
                
                processedBlocks++;
                rebuildProgress.ProcessedBlocks = processedBlocks;
                rebuildProgress.PercentComplete = (processedBlocks * 100) / totalBlocks;
                
                progress?.Report(rebuildProgress);
            }
            
            _logger.LogInfo($"Index rebuild completed. Processed {processedBlocks} blocks.");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Index rebuild failed: {ex.Message}");
            return Result.Failure($"Rebuild failed: {ex.Message}");
        }
    }
    
    private async Task ProcessEmailBatchBlock(Block block, long offset)
    {
        try
        {
            // Deserialize email batch
            var emails = DeserializeEmailBatch(block.Payload);
            
            foreach (var (emailData, localId) in emails)
            {
                var message = MimeMessage.Load(new MemoryStream(emailData));
                
                var emailId = new EmailHashedID
                {
                    BlockId = block.Id,
                    LocalId = localId,
                    EnvelopeHash = EmailHashedID.ComputeEnvelopeHash(message),
                    ContentHash = EmailHashedID.ComputeContentHash(emailData)
                };
                
                // Index the email (folder association will be found from folder blocks)
                await _indexManager.IndexEmailAsync(emailId, message, "", 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to process email batch block: {ex.Message}");
        }
    }
    
    private async Task ProcessFolderBlock(Block block, long offset)
    {
        try
        {
            var folderResult = _serializer.Deserialize<FolderContent>(
                block.Payload, 
                block.PayloadEncoding);
                
            if (folderResult.IsSuccess)
            {
                var folder = folderResult.Value;
                await _indexManager.UpdateFolderIndexAsync(folder.Name, block.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to process folder block: {ex.Message}");
        }
    }
    
    private async Task ProcessEnvelopeBlock(Block block, long offset)
    {
        try
        {
            var envelopeResult = _serializer.Deserialize<FolderEnvelopeBlock>(
                block.Payload,
                block.PayloadEncoding);
                
            if (envelopeResult.IsSuccess)
            {
                var envelopeBlock = envelopeResult.Value;
                
                // Update envelope locations for all emails in this block
                foreach (var envelope in envelopeBlock.Envelopes)
                {
                    // Parse compound key to update envelope location
                    var parts = envelope.CompoundId.Split(':');
                    if (parts.Length == 2)
                    {
                        await _indexManager.UpdateEnvelopeLocationAsync(
                            envelope.CompoundId, 
                            block.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to process envelope block: {ex.Message}");
        }
    }
    
    private List<(byte[] data, int localId)> DeserializeEmailBatch(byte[] payload)
    {
        var emails = new List<(byte[], int)>();
        
        using var ms = new MemoryStream(payload);
        using var reader = new BinaryReader(ms);
        
        var count = reader.ReadInt32();
        
        // Read table of contents
        var entries = new List<(int length, int localId)>();
        for (int i = 0; i < count; i++)
        {
            var length = reader.ReadInt32();
            reader.ReadBytes(32); // Skip envelope hash
            reader.ReadBytes(32); // Skip content hash
            entries.Add((length, i));
        }
        
        // Read email data
        foreach (var (length, localId) in entries)
        {
            var data = reader.ReadBytes(length);
            emails.Add((data, localId));
        }
        
        return emails;
    }
}

public class RebuildProgress
{
    public string Phase { get; set; }
    public int TotalBlocks { get; set; }
    public int ProcessedBlocks { get; set; }
    public int PercentComplete { get; set; }
    public string CurrentOperation { get; set; }
}
```

## Section 3.2: Search Optimization

### Task 3.2.1: Create Search Optimizer
**File**: `EmailDB.Format/Search/SearchOptimizer.cs`
**Dependencies**: IndexManager, FolderManager
**Description**: Optimizes search operations using envelope blocks

```csharp
namespace EmailDB.Format.Search;

/// <summary>
/// Optimizes search operations using indexes and envelope blocks.
/// </summary>
public class SearchOptimizer
{
    private readonly IndexManager _indexManager;
    private readonly FolderManager _folderManager;
    private readonly RawBlockManager _blockManager;
    private readonly IBlockContentSerializer _serializer;
    private readonly ILogger _logger;
    
    // Cache for envelope blocks
    private readonly LRUCache<long, FolderEnvelopeBlock> _envelopeCache;
    
    public SearchOptimizer(
        IndexManager indexManager,
        FolderManager folderManager,
        RawBlockManager blockManager,
        IBlockContentSerializer serializer,
        ILogger logger = null)
    {
        _indexManager = indexManager;
        _folderManager = folderManager;
        _blockManager = blockManager;
        _serializer = serializer;
        _logger = logger ?? new ConsoleLogger();
        
        // Initialize cache with 100 envelope blocks
        _envelopeCache = new LRUCache<long, FolderEnvelopeBlock>(100);
    }
    
    /// <summary>
    /// Performs optimized full-text search.
    /// </summary>
    public async Task<Result<List<SearchResult>>> SearchAsync(
        string query,
        SearchOptions options = null)
    {
        options ??= new SearchOptions();
        
        try
        {
            // Parse query into search terms
            var searchTerms = ParseQuery(query);
            if (searchTerms.Count == 0)
                return Result<List<SearchResult>>.Success(new List<SearchResult>());
            
            // Find matching emails
            var matches = await FindMatchingEmailsAsync(searchTerms, options);
            
            // Score and rank results
            var scoredResults = await ScoreResultsAsync(matches, searchTerms);
            
            // Load envelope data for top results
            var results = await LoadSearchResultsAsync(
                scoredResults
                    .OrderByDescending(r => r.Value)
                    .Take(options.MaxResults)
                    .Select(r => r.Key)
                    .ToList());
            
            return Result<List<SearchResult>>.Success(results);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Search failed: {ex.Message}");
            return Result<List<SearchResult>>.Failure($"Search error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Performs advanced search with multiple criteria.
    /// </summary>
    public async Task<Result<List<SearchResult>>> AdvancedSearchAsync(
        AdvancedSearchQuery query,
        SearchOptions options = null)
    {
        options ??= new SearchOptions();
        
        try
        {
            var candidateEmails = new HashSet<string>();
            var mustMatch = new List<HashSet<string>>();
            
            // Search by sender
            if (!string.IsNullOrEmpty(query.From))
            {
                var fromMatches = await SearchByFieldAsync("from", query.From);
                mustMatch.Add(fromMatches);
            }
            
            // Search by recipient
            if (!string.IsNullOrEmpty(query.To))
            {
                var toMatches = await SearchByFieldAsync("to", query.To);
                mustMatch.Add(toMatches);
            }
            
            // Search by subject
            if (!string.IsNullOrEmpty(query.Subject))
            {
                var subjectMatches = await SearchByTermsAsync(
                    ParseQuery(query.Subject));
                mustMatch.Add(subjectMatches);
            }
            
            // Date range filter
            HashSet<string> dateFiltered = null;
            if (query.StartDate.HasValue || query.EndDate.HasValue)
            {
                dateFiltered = await FilterByDateRangeAsync(
                    query.StartDate,
                    query.EndDate);
                mustMatch.Add(dateFiltered);
            }
            
            // Combine all criteria (intersection)
            if (mustMatch.Count > 0)
            {
                candidateEmails = mustMatch[0];
                for (int i = 1; i < mustMatch.Count; i++)
                {
                    candidateEmails.IntersectWith(mustMatch[i]);
                }
            }
            
            // Apply folder filter if specified
            if (!string.IsNullOrEmpty(query.Folder))
            {
                candidateEmails = await FilterByFolderAsync(
                    candidateEmails, 
                    query.Folder);
            }
            
            // Load results
            var results = await LoadSearchResultsAsync(
                candidateEmails
                    .Take(options.MaxResults)
                    .ToList());
            
            return Result<List<SearchResult>>.Success(results);
        }
        catch (Exception ex)
        {
            return Result<List<SearchResult>>.Failure($"Advanced search error: {ex.Message}");
        }
    }
    
    private List<string> ParseQuery(string query)
    {
        // Simple tokenization - could be enhanced with proper query parsing
        var terms = new List<string>();
        var tokens = query.ToLowerInvariant().Split(
            new[] { ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries);
            
        foreach (var token in tokens)
        {
            if (token.Length >= 3 && !IsStopWord(token))
            {
                terms.Add(token);
            }
        }
        
        return terms;
    }
    
    private async Task<Dictionary<string, int>> FindMatchingEmailsAsync(
        List<string> searchTerms,
        SearchOptions options)
    {
        var matches = new Dictionary<string, int>();
        
        foreach (var term in searchTerms)
        {
            var termMatches = _indexManager.GetEmailsBySearchTerm(term);
            if (termMatches.IsSuccess)
            {
                foreach (var emailId in termMatches.Value)
                {
                    if (!matches.ContainsKey(emailId))
                        matches[emailId] = 0;
                    matches[emailId]++;
                }
            }
        }
        
        return matches;
    }
    
    private async Task<Dictionary<string, float>> ScoreResultsAsync(
        Dictionary<string, int> matches,
        List<string> searchTerms)
    {
        var scores = new Dictionary<string, float>();
        
        foreach (var (emailId, matchCount) in matches)
        {
            // Simple TF scoring
            float score = (float)matchCount / searchTerms.Count;
            
            // Boost recent emails
            var locationResult = _indexManager.GetEmailLocation(emailId);
            if (locationResult.IsSuccess)
            {
                // Could add recency boost based on block ID (newer = higher)
                score *= 1.0f + (locationResult.Value.BlockId / 1000000.0f);
            }
            
            scores[emailId] = score;
        }
        
        return scores;
    }
    
    private async Task<List<SearchResult>> LoadSearchResultsAsync(
        List<string> emailIds)
    {
        var results = new List<SearchResult>();
        
        // Group by envelope block for efficient loading
        var envelopeGroups = new Dictionary<long, List<string>>();
        
        foreach (var emailId in emailIds)
        {
            var envelopeLocResult = _indexManager.GetEnvelopeBlockLocation(emailId);
            if (envelopeLocResult.IsSuccess)
            {
                if (!envelopeGroups.ContainsKey(envelopeLocResult.Value))
                    envelopeGroups[envelopeLocResult.Value] = new List<string>();
                    
                envelopeGroups[envelopeLocResult.Value].Add(emailId);
            }
        }
        
        // Load envelope blocks
        foreach (var (envelopeBlockId, groupEmailIds) in envelopeGroups)
        {
            var envelopeBlock = await LoadEnvelopeBlockAsync(envelopeBlockId);
            if (envelopeBlock != null)
            {
                foreach (var emailId in groupEmailIds)
                {
                    var envelope = envelopeBlock.Envelopes
                        .FirstOrDefault(e => e.CompoundId == emailId);
                        
                    if (envelope != null)
                    {
                        results.Add(new SearchResult
                        {
                            EmailId = EmailHashedID.FromCompoundKey(emailId),
                            Envelope = envelope,
                            Preview = GeneratePreview(envelope)
                        });
                    }
                }
            }
        }
        
        return results;
    }
    
    private async Task<FolderEnvelopeBlock> LoadEnvelopeBlockAsync(long blockId)
    {
        // Check cache first
        if (_envelopeCache.TryGet(blockId, out var cached))
            return cached;
        
        // Load from disk
        var blockResult = await _blockManager.ReadBlockAsync(blockId);
        if (!blockResult.IsSuccess)
            return null;
        
        var deserializeResult = _serializer.Deserialize<FolderEnvelopeBlock>(
            blockResult.Value.Payload,
            blockResult.Value.PayloadEncoding);
            
        if (!deserializeResult.IsSuccess)
            return null;
        
        var envelopeBlock = deserializeResult.Value;
        _envelopeCache.Set(blockId, envelopeBlock);
        
        return envelopeBlock;
    }
    
    private string GeneratePreview(EmailEnvelope envelope)
    {
        // Generate a preview from envelope data
        return $"{envelope.Subject} - {envelope.From} ({envelope.Date:d})";
    }
    
    private bool IsStopWord(string word)
    {
        // Common stop words to ignore
        var stopWords = new HashSet<string> 
        { 
            "the", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "from", "up", "about", "into", "through"
        };
        
        return stopWords.Contains(word);
    }
}

// Supporting classes
public class SearchOptions
{
    public int MaxResults { get; set; } = 50;
    public bool IncludeDeleted { get; set; } = false;
    public string[] FolderFilter { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public class AdvancedSearchQuery
{
    public string From { get; set; }
    public string To { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Folder { get; set; }
    public string[] Tags { get; set; }
}

public class SearchResult
{
    public EmailHashedID EmailId { get; set; }
    public EmailEnvelope Envelope { get; set; }
    public string Preview { get; set; }
    public float Score { get; set; }
    public List<string> MatchedTerms { get; set; }
}
```

### Task 3.2.2: Create LRU Cache for Envelopes
**File**: `EmailDB.Format/Caching/LRUCache.cs`
**Dependencies**: None
**Description**: Efficient cache for frequently accessed envelope blocks

```csharp
namespace EmailDB.Format.Caching;

/// <summary>
/// Thread-safe LRU (Least Recently Used) cache implementation.
/// </summary>
public class LRUCache<TKey, TValue>
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly ReaderWriterLockSlim _lock;
    
    public LRUCache(int capacity)
    {
        _capacity = capacity;
        _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
        _lock = new ReaderWriterLockSlim();
    }
    
    public bool TryGet(TKey key, out TValue value)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lock.EnterWriteLock();
                try
                {
                    // Move to front (most recently used)
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
                
                value = node.Value.Value;
                return true;
            }
            
            value = default;
            return false;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }
    
    public void Set(TKey key, TValue value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                // Update existing
                node.Value.Value = value;
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
            else
            {
                // Add new
                if (_cache.Count >= _capacity)
                {
                    // Remove least recently used
                    var lru = _lruList.Last;
                    _cache.Remove(lru.Value.Key);
                    _lruList.RemoveLast();
                }
                
                var cacheItem = new CacheItem { Key = key, Value = value };
                var newNode = _lruList.AddFirst(cacheItem);
                _cache[key] = newNode;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _lruList.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    private class CacheItem
    {
        public TKey Key { get; set; }
        public TValue Value { get; set; }
    }
}
```

### Task 3.2.3: Update EmailManager for Optimized Search
**File**: `EmailDB.Format/FileManagement/EmailManager.cs` (modifications)
**Description**: Integrate SearchOptimizer into EmailManager

```csharp
public partial class EmailManager
{
    private readonly SearchOptimizer _searchOptimizer;
    
    // Update constructor to include SearchOptimizer
    
    /// <summary>
    /// Searches emails using optimized search with envelope previews.
    /// </summary>
    public async Task<Result<List<EmailSearchResult>>> SearchAsync(
        string searchTerm, 
        int maxResults = 50)
    {
        var searchOptions = new SearchOptions { MaxResults = maxResults };
        var searchResult = await _searchOptimizer.SearchAsync(searchTerm, searchOptions);
        
        if (!searchResult.IsSuccess)
            return Result<List<EmailSearchResult>>.Failure(searchResult.Error);
        
        // Convert to EmailSearchResult format
        var results = searchResult.Value.Select(r => new EmailSearchResult
        {
            EmailId = r.EmailId,
            Envelope = r.Envelope,
            RelevanceScore = r.Score,
            MatchedFields = r.MatchedTerms
        }).ToList();
        
        return Result<List<EmailSearchResult>>.Success(results);
    }
    
    /// <summary>
    /// Performs advanced search with multiple criteria.
    /// </summary>
    public async Task<Result<List<EmailSearchResult>>> AdvancedSearchAsync(
        SearchQuery query)
    {
        var advancedQuery = new AdvancedSearchQuery
        {
            From = query.From,
            To = query.To,
            Subject = query.Subject,
            StartDate = query.StartDate,
            EndDate = query.EndDate,
            Folder = query.Folder
        };
        
        var searchResult = await _searchOptimizer.AdvancedSearchAsync(advancedQuery);
        
        if (!searchResult.IsSuccess)
            return Result<List<EmailSearchResult>>.Failure(searchResult.Error);
        
        var results = searchResult.Value.Select(r => new EmailSearchResult
        {
            EmailId = r.EmailId,
            Envelope = r.Envelope,
            RelevanceScore = r.Score,
            MatchedFields = new List<string>()
        }).ToList();
        
        return Result<List<EmailSearchResult>>.Success(results);
    }
}
```

## Implementation Timeline

### Week 1: Index Infrastructure (Days 1-5)
**Day 1-2: Core Index Management**
- [ ] Task 3.1.1: Create IndexManager class
- [ ] Task 3.1.2: Implement custom serializers
- [ ] Set up index directory structure

**Day 3-4: Integration**
- [ ] Task 3.1.3: Update HybridEmailStore to use IndexManager
- [ ] Migrate existing index operations
- [ ] Test index operations

**Day 5: Index Rebuilding**
- [ ] Task 3.1.4: Implement IndexRebuilder
- [ ] Create progress reporting
- [ ] Test rebuild functionality

### Week 2: Search Optimization (Days 6-10)
**Day 6-7: Search Infrastructure**
- [ ] Task 3.2.1: Create SearchOptimizer
- [ ] Implement query parsing
- [ ] Create scoring algorithms

**Day 8: Caching**
- [ ] Task 3.2.2: Implement LRU cache
- [ ] Integrate envelope caching
- [ ] Performance testing

**Day 9-10: Integration**
- [ ] Task 3.2.3: Update EmailManager
- [ ] Implement advanced search
- [ ] Create search result previews

### Week 3: Testing and Optimization (Days 11-15)
**Day 11-12: Performance Testing**
- [ ] Benchmark search performance
- [ ] Optimize hot paths
- [ ] Cache tuning

**Day 13-14: Integration Testing**
- [ ] End-to-end search tests
- [ ] Index consistency tests
- [ ] Concurrent access tests

**Day 15: Documentation**
- [ ] API documentation
- [ ] Performance characteristics
- [ ] Best practices guide

## Performance Targets

1. **Index Operations**
   - Single email indexing: <5ms
   - Bulk indexing: >10,000 emails/second
   - Index lookup: <1ms

2. **Search Performance**
   - Simple search: <50ms for 1M emails
   - Advanced search: <100ms for 1M emails
   - Preview generation: <10ms per result

3. **Memory Usage**
   - Index memory: <500MB for 1M emails
   - Envelope cache: <100MB
   - Search working set: <50MB

## Integration Points

### With Phase 1
- BlockLocation types for index storage
- Compression for index segments
- Encryption for sensitive indexes

### With Phase 2
- IndexManager used by EmailManager
- SearchOptimizer integrated with HybridEmailStore
- FolderManager provides envelope blocks

## Success Criteria

1. **All indexes store references only** (no data duplication)
2. **Search preview without loading emails** (using envelopes)
3. **Index rebuild capability** for disaster recovery
4. **Sub-second search performance** at scale
5. **Memory-efficient caching** with LRU eviction
6. **Thread-safe concurrent access** to all indexes

## Risk Mitigation

1. **Index Corruption**: Implement consistency checks and rebuild
2. **Performance Degradation**: Monitor and optimize hot paths
3. **Memory Pressure**: Configurable cache sizes and eviction
4. **Concurrent Access**: Proper locking and thread safety
5. **Search Quality**: Tunable scoring algorithms