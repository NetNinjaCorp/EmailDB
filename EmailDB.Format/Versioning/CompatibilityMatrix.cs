using System;
using System.Collections.Generic;
using System.Linq;
using EmailDB.Format.Models;

namespace EmailDB.Format.Versioning;

/// <summary>
/// Defines compatibility rules and feature support across database versions.
/// </summary>
public static class CompatibilityMatrix
{
    /// <summary>
    /// Feature support matrix across versions.
    /// </summary>
    public static readonly Dictionary<DatabaseVersion, VersionFeatureSet> FeatureMatrix = new()
    {
        [new DatabaseVersion(1, 0, 0)] = new VersionFeatureSet
        {
            Version = new DatabaseVersion(1, 0, 0),
            SupportedOperations = new[]
            {
                DatabaseOperation.BasicEMLImport,
                DatabaseOperation.FolderHierarchy,
                DatabaseOperation.FullTextSearch
            },
            SupportedBlockTypes = new[]
            {
                BlockType.Metadata,
                BlockType.Folder,
                BlockType.EmailBatch
            },
            MaximumEmailsPerBatch = 1000,
            MaximumDatabaseSize = 10L * 1024 * 1024 * 1024, // 10GB
            CompressionSupported = false,
            EncryptionSupported = false,
            ConcurrentReadersSupported = true,
            ConcurrentWritersSupported = false
        },
        
        [new DatabaseVersion(1, 1, 0)] = new VersionFeatureSet
        {
            Version = new DatabaseVersion(1, 1, 0),
            SupportedOperations = new[]
            {
                DatabaseOperation.BasicEMLImport,
                DatabaseOperation.FolderHierarchy,
                DatabaseOperation.FullTextSearch,
                DatabaseOperation.Compression
            },
            SupportedBlockTypes = new[]
            {
                BlockType.Metadata,
                BlockType.Folder,
                BlockType.EmailBatch
            },
            MaximumEmailsPerBatch = 5000,
            MaximumDatabaseSize = 50L * 1024 * 1024 * 1024, // 50GB
            CompressionSupported = true,
            EncryptionSupported = false,
            ConcurrentReadersSupported = true,
            ConcurrentWritersSupported = false
        },
        
        [new DatabaseVersion(2, 0, 0)] = new VersionFeatureSet
        {
            Version = new DatabaseVersion(2, 0, 0),
            SupportedOperations = new[]
            {
                DatabaseOperation.BasicEMLImport,
                DatabaseOperation.FolderHierarchy,
                DatabaseOperation.FullTextSearch,
                DatabaseOperation.Compression,
                DatabaseOperation.EmailBatching,
                DatabaseOperation.BlockSuperseding,
                DatabaseOperation.EnvelopeBlocks
            },
            SupportedBlockTypes = new[]
            {
                BlockType.Metadata,
                BlockType.Folder,
                BlockType.EmailBatch,
                BlockType.FolderEnvelope,
                BlockType.KeyManager,
                BlockType.KeyExchange
            },
            MaximumEmailsPerBatch = 10000,
            MaximumDatabaseSize = 1L * 1024 * 1024 * 1024 * 1024, // 1TB
            CompressionSupported = true,
            EncryptionSupported = true,
            ConcurrentReadersSupported = true,
            ConcurrentWritersSupported = true
        }
    };
    
    /// <summary>
    /// Compatibility rules between versions.
    /// </summary>
    public static readonly Dictionary<(DatabaseVersion from, DatabaseVersion to), CompatibilityRule> CompatibilityRules = new()
    {
        // Same version
        [(new DatabaseVersion(1, 0, 0), new DatabaseVersion(1, 0, 0))] = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = false,
            CanDirectlyOpen = true,
            Notes = "Same version - fully compatible"
        },
        
        [(new DatabaseVersion(2, 0, 0), new DatabaseVersion(2, 0, 0))] = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = false,
            CanDirectlyOpen = true,
            Notes = "Same version - fully compatible"
        },
        
        // Minor version upgrades (backward compatible)
        [(new DatabaseVersion(1, 0, 0), new DatabaseVersion(1, 1, 0))] = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = false,
            CanDirectlyOpen = true,
            Notes = "Minor upgrade - new features available but not required"
        },
        
        // Minor version downgrades (forward compatible)
        [(new DatabaseVersion(1, 1, 0), new DatabaseVersion(1, 0, 0))] = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = false,
            CanDirectlyOpen = true,
            Notes = "Minor downgrade - some features may not be available"
        },
        
        // Major version upgrades
        [(new DatabaseVersion(1, 0, 0), new DatabaseVersion(2, 0, 0))] = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = true,
            CanDirectlyOpen = false,
            Notes = "Major upgrade - requires migration for new features"
        },
        
        [(new DatabaseVersion(1, 1, 0), new DatabaseVersion(2, 0, 0))] = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = true,
            CanDirectlyOpen = false,
            Notes = "Major upgrade - requires migration for new features"
        },
        
        // Major version downgrades (not supported)
        [(new DatabaseVersion(2, 0, 0), new DatabaseVersion(1, 0, 0))] = new CompatibilityRule
        {
            IsCompatible = false,
            RequiresMigration = false,
            CanDirectlyOpen = false,
            Notes = "Major downgrade not supported"
        },
        
        [(new DatabaseVersion(2, 0, 0), new DatabaseVersion(1, 1, 0))] = new CompatibilityRule
        {
            IsCompatible = false,
            RequiresMigration = false,
            CanDirectlyOpen = false,
            Notes = "Major downgrade not supported"
        }
    };
    
    /// <summary>
    /// Gets the feature set for a specific database version.
    /// </summary>
    public static VersionFeatureSet GetFeatureSet(DatabaseVersion version)
    {
        // Try exact match first
        if (FeatureMatrix.TryGetValue(version, out var exact))
        {
            return exact;
        }
        
        // Find closest compatible version
        var compatible = FeatureMatrix.Keys
            .Where(v => v.Major == version.Major && v <= version)
            .OrderByDescending(v => v)
            .FirstOrDefault();
            
        if (compatible != null)
        {
            return FeatureMatrix[compatible];
        }
        
        // Fallback to minimum supported version
        return FeatureMatrix[DatabaseVersion.MinimumSupported];
    }
    
    /// <summary>
    /// Gets compatibility information between two versions.
    /// </summary>
    public static CompatibilityRule GetCompatibilityRule(DatabaseVersion from, DatabaseVersion to)
    {
        // Try exact match first
        if (CompatibilityRules.TryGetValue((from, to), out var exact))
        {
            return exact;
        }
        
        // Apply general compatibility rules
        if (from == to)
        {
            return new CompatibilityRule
            {
                IsCompatible = true,
                RequiresMigration = false,
                CanDirectlyOpen = true,
                Notes = "Same version"
            };
        }
        
        if (from.Major == to.Major)
        {
            // Same major version
            return new CompatibilityRule
            {
                IsCompatible = true,
                RequiresMigration = false,
                CanDirectlyOpen = true,
                Notes = from < to ? "Minor upgrade" : "Minor downgrade"
            };
        }
        
        if (to.Major == from.Major + 1)
        {
            // Next major version
            return new CompatibilityRule
            {
                IsCompatible = true,
                RequiresMigration = true,
                CanDirectlyOpen = false,
                Notes = "Major upgrade requires migration"
            };
        }
        
        if (to.Major < from.Major)
        {
            // Downgrade
            return new CompatibilityRule
            {
                IsCompatible = false,
                RequiresMigration = false,
                CanDirectlyOpen = false,
                Notes = "Downgrade not supported"
            };
        }
        
        // Multiple major version jump
        return new CompatibilityRule
        {
            IsCompatible = false,
            RequiresMigration = false,
            CanDirectlyOpen = false,
            Notes = "Cannot skip major versions"
        };
    }
    
    /// <summary>
    /// Validates if an operation is supported in a specific version.
    /// </summary>
    public static bool IsOperationSupported(DatabaseVersion version, DatabaseOperation operation)
    {
        var featureSet = GetFeatureSet(version);
        return featureSet.SupportedOperations.Contains(operation);
    }
    
    /// <summary>
    /// Validates if a block type is supported in a specific version.
    /// </summary>
    public static bool IsBlockTypeSupported(DatabaseVersion version, BlockType blockType)
    {
        var featureSet = GetFeatureSet(version);
        return featureSet.SupportedBlockTypes.Contains(blockType);
    }
    
    /// <summary>
    /// Gets the upgrade path from one version to another.
    /// </summary>
    public static List<DatabaseVersion> GetUpgradePath(DatabaseVersion from, DatabaseVersion to)
    {
        var path = new List<DatabaseVersion>();
        
        if (from >= to)
        {
            return path; // No upgrade needed or not possible
        }
        
        var current = from;
        while (current < to)
        {
            var nextMajor = new DatabaseVersion(current.Major + 1, 0, 0);
            if (nextMajor <= to)
            {
                path.Add(nextMajor);
                current = nextMajor;
            }
            else
            {
                path.Add(to);
                break;
            }
        }
        
        return path;
    }
}

/// <summary>
/// Feature set for a specific database version.
/// </summary>
public class VersionFeatureSet
{
    public DatabaseVersion Version { get; set; }
    public DatabaseOperation[] SupportedOperations { get; set; } = Array.Empty<DatabaseOperation>();
    public BlockType[] SupportedBlockTypes { get; set; } = Array.Empty<BlockType>();
    public int MaximumEmailsPerBatch { get; set; }
    public long MaximumDatabaseSize { get; set; }
    public bool CompressionSupported { get; set; }
    public bool EncryptionSupported { get; set; }
    public bool ConcurrentReadersSupported { get; set; }
    public bool ConcurrentWritersSupported { get; set; }
}

/// <summary>
/// Compatibility rule between two database versions.
/// </summary>
public class CompatibilityRule
{
    public bool IsCompatible { get; set; }
    public bool RequiresMigration { get; set; }
    public bool CanDirectlyOpen { get; set; }
    public string Notes { get; set; } = "";
}