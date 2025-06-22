using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.Versioning;
using EmailDB.Format.Models;

namespace EmailDB.Format;

/// <summary>
/// Version-aware search functionality for EmailDatabase.
/// </summary>
public partial class EmailDatabase
{
    /// <summary>
    /// Search emails with version-aware feature detection.
    /// </summary>
    public async Task<Result<List<VersionAwareSearchResult>>> SearchWithVersionAwarenessAsync(
        string searchTerm, 
        SearchOptions options = null)
    {
        try
        {
            options ??= new SearchOptions();
            var currentVersion = DatabaseVersion ?? DatabaseVersion.Current;
            
            // Check if search is supported in this version
            if (!CompatibilityMatrix.IsOperationSupported(currentVersion, DatabaseOperation.FullTextSearch))
            {
                return Result<List<VersionAwareSearchResult>>.Failure(
                    $"Full-text search is not supported in database version {currentVersion}");
            }
            
            var results = new List<VersionAwareSearchResult>();
            var featureSet = CompatibilityMatrix.GetFeatureSet(currentVersion);
            
            // Determine available search features based on version
            var availableFeatures = GetAvailableSearchFeatures(currentVersion);
            
            // Perform search based on available features
            if (availableFeatures.SupportsAdvancedSearch && !string.IsNullOrEmpty(options.AdvancedQuery))
            {
                // Use advanced search if available and requested
                var advancedResults = await PerformAdvancedSearchAsync(options.AdvancedQuery, options, currentVersion);
                results.AddRange(advancedResults);
            }
            else
            {
                // Fall back to basic search
                var basicResults = await PerformBasicSearchAsync(searchTerm, options, currentVersion);
                results.AddRange(basicResults);
            }
            
            // Apply version-specific result filtering and ranking
            results = ApplyVersionSpecificFiltering(results, currentVersion, options);
            
            // Limit results based on version capabilities
            var maxResults = Math.Min(options.MaxResults, featureSet.MaximumEmailsPerBatch);
            results = results.Take(maxResults).ToList();
            
            return Result<List<VersionAwareSearchResult>>.Success(results);
        }
        catch (Exception ex)
        {
            return Result<List<VersionAwareSearchResult>>.Failure($"Version-aware search failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Search with automatic fallback based on version capabilities.
    /// </summary>
    public async Task<Result<List<VersionAwareSearchResult>>> SmartSearchAsync(
        string query, 
        SearchOptions options = null)
    {
        try
        {
            options ??= new SearchOptions();
            var currentVersion = DatabaseVersion ?? DatabaseVersion.Current;
            var availableFeatures = GetAvailableSearchFeatures(currentVersion);
            
            // Auto-detect query type and use best available method
            if (availableFeatures.SupportsAdvancedSearch && IsAdvancedQuery(query))
            {
                options.AdvancedQuery = query;
                return await SearchWithVersionAwarenessAsync("", options);
            }
            else
            {
                return await SearchWithVersionAwarenessAsync(query, options);
            }
        }
        catch (Exception ex)
        {
            return Result<List<VersionAwareSearchResult>>.Failure($"Smart search failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets available search features for a database version.
    /// </summary>
    public SearchFeatureSet GetAvailableSearchFeatures(DatabaseVersion version = null)
    {
        version ??= DatabaseVersion ?? DatabaseVersion.Current;
        var featureSet = CompatibilityMatrix.GetFeatureSet(version);
        
        return new SearchFeatureSet
        {
            SupportsBasicSearch = featureSet.SupportedOperations.Contains(DatabaseOperation.FullTextSearch),
            SupportsAdvancedSearch = version.Major >= 2,
            SupportsRegexSearch = version.Major >= 2,
            SupportsFuzzySearch = version.Major >= 2 && version.Minor >= 1,
            SupportsFieldSearch = true,
            SupportsDateRangeFiltering = version.Major >= 2,
            SupportsSizeFiltering = version.Major >= 2,
            SupportsAttachmentSearch = featureSet.SupportedOperations.Contains(DatabaseOperation.EnvelopeBlocks),
            MaxSearchTerms = version.Major >= 2 ? 50 : 10,
            MaxResultsPerPage = featureSet.MaximumEmailsPerBatch / 10,
            SupportsParallelSearch = featureSet.ConcurrentReadersSupported
        };
    }
    
    private async Task<List<VersionAwareSearchResult>> PerformBasicSearchAsync(
        string searchTerm, 
        SearchOptions options, 
        DatabaseVersion version)
    {
        var results = new List<VersionAwareSearchResult>();
        var searchTermLower = searchTerm.ToLowerInvariant();
        
        // Use existing search functionality but wrap results
        var basicResults = await SearchAsync(searchTermLower, options.MaxResults);
        
        foreach (var result in basicResults)
        {
            results.Add(new VersionAwareSearchResult
            {
                EmailId = result.EmailId,
                Subject = result.Subject,
                From = result.From,
                Date = result.Date,
                RelevanceScore = result.RelevanceScore,
                MatchedFields = result.MatchedFields,
                DatabaseVersion = version,
                SearchMethod = "Basic",
                AvailableFeatures = GetAvailableSearchFeatures(version)
            });
        }
        
        return results;
    }
    
    private async Task<List<VersionAwareSearchResult>> PerformAdvancedSearchAsync(
        string advancedQuery, 
        SearchOptions options, 
        DatabaseVersion version)
    {
        var results = new List<VersionAwareSearchResult>();
        
        // Parse advanced query (simplified implementation)
        var queryParts = ParseAdvancedQuery(advancedQuery);
        
        foreach (var part in queryParts)
        {
            var partResults = await PerformBasicSearchAsync(part.Term, options, version);
            
            // Apply query part specific logic
            foreach (var result in partResults)
            {
                result.SearchMethod = "Advanced";
                result.RelevanceScore *= part.Weight;
                
                if (!results.Any(r => r.EmailId.Equals(result.EmailId)))
                {
                    results.Add(result);
                }
            }
        }
        
        return results.OrderByDescending(r => r.RelevanceScore).ToList();
    }
    
    private List<VersionAwareSearchResult> ApplyVersionSpecificFiltering(
        List<VersionAwareSearchResult> results, 
        DatabaseVersion version, 
        SearchOptions options)
    {
        var availableFeatures = GetAvailableSearchFeatures(version);
        
        // Apply date filtering if supported and requested
        if (availableFeatures.SupportsDateRangeFiltering && options.DateFrom.HasValue)
        {
            results = results.Where(r => r.Date >= options.DateFrom.Value).ToList();
        }
        
        if (availableFeatures.SupportsDateRangeFiltering && options.DateTo.HasValue)
        {
            results = results.Where(r => r.Date <= options.DateTo.Value).ToList();
        }
        
        // Apply size filtering if supported and requested
        if (availableFeatures.SupportsSizeFiltering && options.MinSize.HasValue)
        {
            // Note: Would need to get email size from metadata
            // This is a placeholder for size-based filtering
        }
        
        return results;
    }
    
    private bool IsAdvancedQuery(string query)
    {
        // Simple heuristics to detect advanced queries
        return query.Contains("AND ") || 
               query.Contains("OR ") || 
               query.Contains("NOT ") ||
               query.Contains("from:") ||
               query.Contains("to:") ||
               query.Contains("subject:") ||
               query.Contains("date:");
    }
    
    private List<QueryPart> ParseAdvancedQuery(string query)
    {
        var parts = new List<QueryPart>();
        
        // Simple parsing - in practice this would be more sophisticated
        var terms = query.Split(new[] { " AND ", " OR " }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var term in terms)
        {
            parts.Add(new QueryPart
            {
                Term = term.Trim(),
                Weight = 1.0f,
                Field = "all"
            });
        }
        
        return parts;
    }
    
    private class QueryPart
    {
        public string Term { get; set; } = "";
        public float Weight { get; set; } = 1.0f;
        public string Field { get; set; } = "all";
    }
}

/// <summary>
/// Search options with version-aware capabilities.
/// </summary>
public class SearchOptions
{
    public int MaxResults { get; set; } = 50;
    public string AdvancedQuery { get; set; } = "";
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public string[] FolderFilter { get; set; } = Array.Empty<string>();
    public bool IncludeAttachments { get; set; } = false;
    public SearchSortOrder SortOrder { get; set; } = SearchSortOrder.Relevance;
}

/// <summary>
/// Sort order for search results.
/// </summary>
public enum SearchSortOrder
{
    Relevance,
    DateAscending,
    DateDescending,
    Subject,
    From,
    Size
}

/// <summary>
/// Version-aware search result.
/// </summary>
public class VersionAwareSearchResult : EmailSearchResult
{
    public DatabaseVersion DatabaseVersion { get; set; }
    public string SearchMethod { get; set; } = "";
    public SearchFeatureSet AvailableFeatures { get; set; }
    public Dictionary<string, object> VersionSpecificData { get; set; } = new();
}

/// <summary>
/// Search features available in a specific database version.
/// </summary>
public class SearchFeatureSet
{
    public bool SupportsBasicSearch { get; set; }
    public bool SupportsAdvancedSearch { get; set; }
    public bool SupportsRegexSearch { get; set; }
    public bool SupportsFuzzySearch { get; set; }
    public bool SupportsFieldSearch { get; set; }
    public bool SupportsDateRangeFiltering { get; set; }
    public bool SupportsSizeFiltering { get; set; }
    public bool SupportsAttachmentSearch { get; set; }
    public int MaxSearchTerms { get; set; }
    public int MaxResultsPerPage { get; set; }
    public bool SupportsParallelSearch { get; set; }
}