using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.Versioning;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using MimeKit;

namespace EmailDB.Format;

/// <summary>
/// Version-aware functionality for EmailDatabase.
/// </summary>
public partial class EmailDatabase
{
    /// <summary>
    /// Import an EML file with version compatibility checking.
    /// </summary>
    public async Task<Result<EmailHashedID>> ImportEMLWithVersionCheckAsync(string emlContent, string fileName = null)
    {
        try
        {
            // Check version compatibility before importing
            var compatibilityResult = await GetVersionCompatibilityAsync();
            if (!compatibilityResult.IsCompatible)
            {
                return Result<EmailHashedID>.Failure(
                    $"Cannot import EML: {compatibilityResult.Message}");
            }
            
            // Check if current version supports the required features
            var requiredCapabilities = FeatureCapabilities.EmailBatching | FeatureCapabilities.FullTextSearch;
            if (!DatabaseVersion.Capabilities.HasFlag(requiredCapabilities))
            {
                return Result<EmailHashedID>.Failure(
                    $"Database version {DatabaseVersion} does not support required features for EML import");
            }
            
            // Perform the import
            var emailId = await ImportEMLAsync(emlContent, fileName);
            
            // Update header with import information if supported
            if (_versionManager != null && DatabaseVersion.Major >= 2)
            {
                await _versionManager.UpdateHeaderAsync(header =>
                {
                    header.Metadata["last_import"] = DateTime.UtcNow.ToString("O");
                    if (header.Metadata.ContainsKey("import_count"))
                    {
                        if (int.TryParse(header.Metadata["import_count"], out var count))
                        {
                            header.Metadata["import_count"] = (count + 1).ToString();
                        }
                    }
                    else
                    {
                        header.Metadata["import_count"] = "1";
                    }
                });
            }
            
            return Result<EmailHashedID>.Success(emailId);
        }
        catch (Exception ex)
        {
            return Result<EmailHashedID>.Failure($"EML import failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Import an EML file from disk with version compatibility checking.
    /// </summary>
    public async Task<Result<EmailHashedID>> ImportEMLFileWithVersionCheckAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Result<EmailHashedID>.Failure($"File not found: {filePath}");
            }
            
            var emlContent = await File.ReadAllTextAsync(filePath);
            var fileName = Path.GetFileName(filePath);
            
            return await ImportEMLWithVersionCheckAsync(emlContent, fileName);
        }
        catch (Exception ex)
        {
            return Result<EmailHashedID>.Failure($"Failed to read EML file: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Import multiple EML files with version compatibility checking and progress reporting.
    /// </summary>
    public async Task<Result<VersionAwareBatchImportResult>> ImportEMLBatchWithVersionCheckAsync(
        (string fileName, string emlContent)[] emails,
        IProgress<BatchImportProgress> progress = null)
    {
        try
        {
            // Check version compatibility
            var compatibilityResult = await GetVersionCompatibilityAsync();
            if (!compatibilityResult.IsCompatible)
            {
                return Result<VersionAwareBatchImportResult>.Failure(
                    $"Cannot import batch: {compatibilityResult.Message}");
            }
            
            var result = new VersionAwareBatchImportResult
            {
                DatabaseVersion = DatabaseVersion,
                ImportStartTime = DateTime.UtcNow
            };
            
            for (int i = 0; i < emails.Length; i++)
            {
                var (fileName, emlContent) = emails[i];
                
                try
                {
                    var importResult = await ImportEMLWithVersionCheckAsync(emlContent, fileName);
                    if (importResult.IsSuccess)
                    {
                        result.SuccessCount++;
                        result.ImportedEmailIds.Add(importResult.Value);
                    }
                    else
                    {
                        result.ErrorCount++;
                        result.Errors.Add($"{fileName}: {importResult.Error}");
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    result.Errors.Add($"{fileName}: {ex.Message}");
                }
                
                // Report progress
                progress?.Report(new BatchImportProgress
                {
                    ProcessedCount = i + 1,
                    TotalCount = emails.Length,
                    SuccessCount = result.SuccessCount,
                    ErrorCount = result.ErrorCount,
                    CurrentFileName = fileName
                });
            }
            
            result.ImportEndTime = DateTime.UtcNow;
            result.TotalDuration = result.ImportEndTime - result.ImportStartTime;
            
            return Result<VersionAwareBatchImportResult>.Success(result);
        }
        catch (Exception ex)
        {
            return Result<VersionAwareBatchImportResult>.Failure($"Batch import failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Validates if a specific operation is supported by the current database version.
    /// </summary>
    public bool IsOperationSupported(DatabaseOperation operation)
    {
        return operation switch
        {
            DatabaseOperation.BasicEMLImport => DatabaseVersion.Major >= 1,
            DatabaseOperation.FullTextSearch => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.FullTextSearch),
            DatabaseOperation.EmailBatching => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.EmailBatching),
            DatabaseOperation.FolderHierarchy => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.FolderHierarchy),
            DatabaseOperation.Compression => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.Compression),
            DatabaseOperation.BlockSuperseding => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.BlockSuperseding),
            DatabaseOperation.InBandKeyManagement => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.InBandKeyManagement),
            DatabaseOperation.EnvelopeBlocks => DatabaseVersion.Capabilities.HasFlag(FeatureCapabilities.EnvelopeBlocks),
            _ => false
        };
    }
    
    /// <summary>
    /// Gets detailed version information including supported operations.
    /// </summary>
    public async Task<VersionInfo> GetDetailedVersionInfoAsync()
    {
        var compatibilityResult = await GetVersionCompatibilityAsync();
        
        return new VersionInfo
        {
            DatabaseVersion = DatabaseVersion,
            ImplementationVersion = DatabaseVersion.Current,
            IsCompatible = compatibilityResult.IsCompatible,
            CanUpgrade = compatibilityResult.CanUpgrade,
            UpgradeType = compatibilityResult.UpgradeType,
            SupportedOperations = Enum.GetValues<DatabaseOperation>()
                .Where(op => IsOperationSupported(op))
                .ToList(),
            Capabilities = DatabaseVersion.Capabilities,
            BlockFormatVersions = DatabaseVersion.BlockFormatVersions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }
}

/// <summary>
/// Enhanced batch import result with version information.
/// </summary>
public class VersionAwareBatchImportResult : BatchImportResult
{
    public DatabaseVersion DatabaseVersion { get; set; }
    public DateTime ImportStartTime { get; set; }
    public DateTime ImportEndTime { get; set; }
    public TimeSpan TotalDuration { get; set; }
}

/// <summary>
/// Progress information for batch import operations.
/// </summary>
public class BatchImportProgress
{
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public string CurrentFileName { get; set; } = "";
    public double ProgressPercentage => TotalCount > 0 ? (double)ProcessedCount / TotalCount * 100 : 0;
}

/// <summary>
/// Database operations that can be version-checked.
/// </summary>
public enum DatabaseOperation
{
    BasicEMLImport,
    FullTextSearch,
    EmailBatching,
    FolderHierarchy,
    Compression,
    BlockSuperseding,
    InBandKeyManagement,
    EnvelopeBlocks
}

/// <summary>
/// Detailed version information.
/// </summary>
public class VersionInfo
{
    public DatabaseVersion DatabaseVersion { get; set; }
    public DatabaseVersion ImplementationVersion { get; set; }
    public bool IsCompatible { get; set; }
    public bool CanUpgrade { get; set; }
    public UpgradeType UpgradeType { get; set; }
    public List<DatabaseOperation> SupportedOperations { get; set; } = new();
    public FeatureCapabilities Capabilities { get; set; }
    public Dictionary<BlockType, int> BlockFormatVersions { get; set; } = new();
}