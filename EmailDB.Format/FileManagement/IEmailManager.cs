using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// High-level interface for email operations.
/// </summary>
public interface IEmailManager : IDisposable
{
    // Email operations
    Task<Result<EmailHashedID>> ImportEMLAsync(string emlContent, string folderPath = "Inbox");
    Task<Result<EmailHashedID>> ImportEMLFileAsync(string filePath, string folderPath = "Inbox");
    Task<Result<BatchImportResult>> ImportEMLBatchAsync((string fileName, string emlContent)[] emails, string folderPath = "Inbox");
    
    // Retrieval operations
    Task<Result<MimeMessage>> GetEmailAsync(EmailHashedID emailId);
    Task<Result<MimeMessage>> GetEmailByMessageIdAsync(string messageId);
    Task<Result<List<EmailEnvelope>>> GetFolderListingAsync(string folderPath);
    
    // Search operations
    Task<Result<List<EmailSearchResult>>> SearchAsync(string searchTerm, int maxResults = 50);
    Task<Result<List<EmailSearchResult>>> AdvancedSearchAsync(SearchQuery query);
    
    // Folder operations
    Task<Result> CreateFolderAsync(string folderPath);
    Task<Result> MoveEmailAsync(EmailHashedID emailId, string fromFolder, string toFolder);
    Task<Result> DeleteEmailAsync(EmailHashedID emailId, bool permanent = false);
    
    // Database operations
    Task<Result<DatabaseStats>> GetDatabaseStatsAsync();
    Task<Result> OptimizeDatabaseAsync();
}

/// <summary>
/// Advanced search query parameters.
/// </summary>
public class SearchQuery
{
    public string Subject { get; set; }
    public string From { get; set; }
    public string To { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string[] Keywords { get; set; }
    public string Folder { get; set; }
}

/// <summary>
/// Email search result with relevance scoring.
/// </summary>
public class EmailSearchResult
{
    public EmailHashedID EmailId { get; set; }
    public EmailEnvelope Envelope { get; set; }
    public float RelevanceScore { get; set; }
    public List<string> MatchedFields { get; set; }
}

/// <summary>
/// Result of a batch email import operation.
/// </summary>
public class BatchImportResult
{
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public List<EmailHashedID> ImportedEmailIds { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Database statistics.
/// </summary>
public class DatabaseStats
{
    public long TotalEmails { get; set; }
    public long TotalBlocks { get; set; }
    public long DatabaseSize { get; set; }
    public int FolderCount { get; set; }
    public Dictionary<string, long> BlockTypeCounts { get; set; }
    public double CompressionRatio { get; set; }
}