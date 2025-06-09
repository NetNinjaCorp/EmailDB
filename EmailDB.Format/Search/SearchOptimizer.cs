using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.Indexing;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Models.EmailContent;
using EmailDB.Format.Caching;

namespace EmailDB.Format.Search;

/// <summary>
/// Optimizes search operations using indexes and envelope blocks.
/// </summary>
public class SearchOptimizer
{
    private readonly IndexManager _indexManager;
    private readonly FolderManager _folderManager;
    private readonly RawBlockManager _blockManager;
    private readonly iBlockContentSerializer _serializer;
    
    // Cache for envelope blocks
    private readonly LRUCache<long, FolderEnvelopeBlock> _envelopeCache;
    
    public SearchOptimizer(
        IndexManager indexManager,
        FolderManager folderManager,
        RawBlockManager blockManager,
        iBlockContentSerializer serializer)
    {
        _indexManager = indexManager;
        _folderManager = folderManager;
        _blockManager = blockManager;
        _serializer = serializer;
        
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
        
        var deserializeResult = _serializer.Deserialize<FolderEnvelopeBlock>(blockResult.Value.Payload);
        if (deserializeResult == null)
            return null;
        
        _envelopeCache.Set(blockId, deserializeResult);
        
        return deserializeResult;
    }
    
    private async Task<HashSet<string>> SearchByFieldAsync(string field, string value)
    {
        // This would be implemented with field-specific indexes
        // For now, use the general search term index
        var result = _indexManager.GetEmailsBySearchTerm(value);
        return result.IsSuccess ? new HashSet<string>(result.Value) : new HashSet<string>();
    }
    
    private async Task<HashSet<string>> SearchByTermsAsync(List<string> terms)
    {
        var result = new HashSet<string>();
        foreach (var term in terms)
        {
            var termResult = _indexManager.GetEmailsBySearchTerm(term);
            if (termResult.IsSuccess)
            {
                foreach (var emailId in termResult.Value)
                {
                    result.Add(emailId);
                }
            }
        }
        return result;
    }
    
    private async Task<HashSet<string>> FilterByDateRangeAsync(DateTime? startDate, DateTime? endDate)
    {
        // This would require a date-based index
        // For now, return empty set (no filtering)
        return new HashSet<string>();
    }
    
    private async Task<HashSet<string>> FilterByFolderAsync(HashSet<string> candidates, string folder)
    {
        // Filter candidates to only those in the specified folder
        // This would require folder-based lookup
        return candidates; // For now, no filtering
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