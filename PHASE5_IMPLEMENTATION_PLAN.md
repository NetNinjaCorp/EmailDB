# Phase 5 Implementation Plan: Format Versioning

## Overview
Phase 5 implements a comprehensive format versioning system that enables the EmailDB to evolve over time while maintaining backward compatibility. This includes version negotiation, capability detection, upgrade paths, and graceful handling of version mismatches.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    Version Management System                      │
├─────────────────────────────────────────────────────────────────┤
│ FormatVersionManager                                              │
│ ├── Version Detection (reads header block)                       │
│ ├── Capability Negotiation (feature flags)                       │
│ ├── Upgrade Coordinator (manages migrations)                     │
│ └── Compatibility Checker (validates operations)                 │
│                                                                   │
│ Version Components                                                │
│ ├── DatabaseVersion (Major.Minor.Patch)                          │
│ ├── FeatureCapabilities (bitflags for features)                  │
│ ├── BlockFormatVersions (per-block-type versions)                │
│ └── CompatibilityMatrix (version relationships)                  │
│                                                                   │
│ Upgrade Infrastructure                                            │
│ ├── UpgradeOrchestrator (coordinates upgrades)                   │
│ ├── MigrationHandlers (version-specific migrations)              │
│ └── UpgradeValidator (ensures integrity)                         │
└─────────────────────────────────────────────────────────────────┘
```

## Section 5.1: Version Management Framework

### Task 5.1.1: Create Version System Core
**File**: `EmailDB.Format/Versioning/DatabaseVersion.cs`
**Dependencies**: None
**Description**: Core version representation and comparison

```csharp
namespace EmailDB.Format.Versioning;

/// <summary>
/// Represents the database format version using semantic versioning.
/// </summary>
public class DatabaseVersion : IComparable<DatabaseVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    
    // Feature capabilities as bitflags
    public FeatureCapabilities Capabilities { get; }
    
    // Block format versions for each block type
    public Dictionary<BlockType, int> BlockFormatVersions { get; }
    
    public DatabaseVersion(int major, int minor, int patch, 
        FeatureCapabilities capabilities = FeatureCapabilities.None)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Capabilities = capabilities;
        BlockFormatVersions = new Dictionary<BlockType, int>();
        
        // Initialize default block versions
        InitializeBlockVersions();
    }
    
    /// <summary>
    /// Current version of the EmailDB format.
    /// </summary>
    public static DatabaseVersion Current => new DatabaseVersion(2, 0, 0,
        FeatureCapabilities.Compression | 
        FeatureCapabilities.Encryption |
        FeatureCapabilities.EmailBatching |
        FeatureCapabilities.EnvelopeBlocks |
        FeatureCapabilities.InBandKeyManagement |
        FeatureCapabilities.HashChainIntegrity);
    
    /// <summary>
    /// Minimum supported version for this implementation.
    /// </summary>
    public static DatabaseVersion MinimumSupported => new DatabaseVersion(1, 0, 0);
    
    private void InitializeBlockVersions()
    {
        // Version 2.0.0 block format versions
        BlockFormatVersions[BlockType.Header] = 2;
        BlockFormatVersions[BlockType.Metadata] = 1;
        BlockFormatVersions[BlockType.Folder] = 2;  // Updated for envelope support
        BlockFormatVersions[BlockType.Email] = 1;   // Deprecated
        BlockFormatVersions[BlockType.EmailBatch] = 1;  // New in v2
        BlockFormatVersions[BlockType.FolderEnvelope] = 1;  // New in v2
        BlockFormatVersions[BlockType.KeyManager] = 1;  // New in v2
        BlockFormatVersions[BlockType.KeyExchange] = 1;  // New in v2
    }
    
    public int CompareTo(DatabaseVersion other)
    {
        if (other == null) return 1;
        
        int majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0) return majorCompare;
        
        int minorCompare = Minor.CompareTo(other.Minor);
        if (minorCompare != 0) return minorCompare;
        
        return Patch.CompareTo(other.Patch);
    }
    
    public bool IsCompatibleWith(DatabaseVersion other)
    {
        if (other == null) return false;
        
        // Same major version = compatible
        if (Major == other.Major) return true;
        
        // Can read older major versions
        if (Major > other.Major && other >= MinimumSupported) return true;
        
        return false;
    }
    
    public bool CanUpgradeTo(DatabaseVersion target)
    {
        if (target == null || this >= target) return false;
        
        // Can upgrade within same major version
        if (Major == target.Major) return true;
        
        // Can upgrade to next major version
        if (target.Major == Major + 1) return true;
        
        return false;
    }
    
    public UpgradeType GetUpgradeType(DatabaseVersion target)
    {
        if (!CanUpgradeTo(target))
            return UpgradeType.NotSupported;
            
        if (Major == target.Major)
            return UpgradeType.InPlace;
            
        return UpgradeType.Migration;
    }
    
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
    
    public static bool operator >=(DatabaseVersion left, DatabaseVersion right)
        => left?.CompareTo(right) >= 0;
        
    public static bool operator >(DatabaseVersion left, DatabaseVersion right)
        => left?.CompareTo(right) > 0;
        
    public static bool operator <=(DatabaseVersion left, DatabaseVersion right)
        => left?.CompareTo(right) <= 0;
        
    public static bool operator <(DatabaseVersion left, DatabaseVersion right)
        => left?.CompareTo(right) < 0;
}

/// <summary>
/// Feature capabilities that can be enabled in a database version.
/// </summary>
[Flags]
public enum FeatureCapabilities : long
{
    None = 0,
    Compression = 1 << 0,
    Encryption = 1 << 1,
    EmailBatching = 1 << 2,
    EnvelopeBlocks = 1 << 3,
    InBandKeyManagement = 1 << 4,
    HashChainIntegrity = 1 << 5,
    FullTextSearch = 1 << 6,
    FolderHierarchy = 1 << 7,
    EmailDeduplication = 1 << 8,
    BlockSuperseding = 1 << 9,
    AtomicTransactions = 1 << 10,
    
    // Reserved for future features
    Reserved1 = 1L << 32,
    Reserved2 = 1L << 33,
    Reserved3 = 1L << 34,
}

public enum UpgradeType
{
    NotSupported,
    InPlace,      // Can upgrade without migration (minor/patch)
    Migration     // Requires data migration (major)
}
```

### Task 5.1.2: Create Format Version Manager
**File**: `EmailDB.Format/Versioning/FormatVersionManager.cs`
**Dependencies**: RawBlockManager, HeaderContent
**Description**: Manages version detection and negotiation

```csharp
namespace EmailDB.Format.Versioning;

/// <summary>
/// Manages database format versions and compatibility.
/// </summary>
public class FormatVersionManager
{
    private readonly RawBlockManager _blockManager;
    private readonly ILogger _logger;
    
    // Cached version information
    private DatabaseVersion _currentVersion;
    private HeaderContent _headerContent;
    private readonly object _versionLock = new();
    
    public FormatVersionManager(RawBlockManager blockManager, ILogger logger = null)
    {
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _logger = logger ?? new ConsoleLogger();
    }
    
    /// <summary>
    /// Detects the database version by reading the header block.
    /// </summary>
    public async Task<Result<DatabaseVersion>> DetectVersionAsync()
    {
        try
        {
            lock (_versionLock)
            {
                if (_currentVersion != null)
                    return Result<DatabaseVersion>.Success(_currentVersion);
            }
            
            // Try to read header block at offset 0
            var headerResult = await _blockManager.ReadBlockAsync(0);
            if (!headerResult.IsSuccess)
            {
                // No header block - this is a new database
                _logger.LogInfo("No header block found - initializing new database");
                return await InitializeNewDatabaseAsync();
            }
            
            var block = headerResult.Value;
            if (block.Type != BlockType.Header)
            {
                return Result<DatabaseVersion>.Failure("Invalid database format: first block is not a header");
            }
            
            // Deserialize header content
            var deserializeResult = DeserializeHeader(block.Payload);
            if (!deserializeResult.IsSuccess)
            {
                return Result<DatabaseVersion>.Failure($"Failed to read header: {deserializeResult.Error}");
            }
            
            _headerContent = deserializeResult.Value;
            
            // Parse version from header
            var version = ParseVersion(_headerContent.FileVersion);
            
            // Detect capabilities from block analysis
            var capabilities = await DetectCapabilitiesAsync();
            version = new DatabaseVersion(version.Major, version.Minor, version.Patch, capabilities);
            
            lock (_versionLock)
            {
                _currentVersion = version;
            }
            
            _logger.LogInfo($"Detected database version: {version}");
            return Result<DatabaseVersion>.Success(version);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to detect database version: {ex.Message}");
            return Result<DatabaseVersion>.Failure($"Version detection failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Validates that an operation is supported by the current version.
    /// </summary>
    public Result ValidateOperation(string operation, FeatureCapabilities requiredCapabilities)
    {
        if (_currentVersion == null)
            return Result.Failure("Database version not detected");
            
        if ((requiredCapabilities & _currentVersion.Capabilities) != requiredCapabilities)
        {
            var missing = requiredCapabilities & ~_currentVersion.Capabilities;
            return Result.Failure($"Operation '{operation}' requires capabilities: {missing}");
        }
        
        return Result.Success();
    }
    
    /// <summary>
    /// Checks if the database can be opened with the current implementation.
    /// </summary>
    public Result<CompatibilityInfo> CheckCompatibility()
    {
        if (_currentVersion == null)
            return Result<CompatibilityInfo>.Failure("Database version not detected");
            
        var info = new CompatibilityInfo
        {
            DatabaseVersion = _currentVersion,
            ImplementationVersion = DatabaseVersion.Current,
            IsCompatible = DatabaseVersion.Current.IsCompatibleWith(_currentVersion),
            CanUpgrade = _currentVersion.CanUpgradeTo(DatabaseVersion.Current),
            UpgradeType = _currentVersion.GetUpgradeType(DatabaseVersion.Current)
        };
        
        if (!info.IsCompatible)
        {
            if (_currentVersion > DatabaseVersion.Current)
            {
                info.Message = $"Database version {_currentVersion} is newer than implementation version {DatabaseVersion.Current}";
            }
            else if (_currentVersion < DatabaseVersion.MinimumSupported)
            {
                info.Message = $"Database version {_currentVersion} is older than minimum supported {DatabaseVersion.MinimumSupported}";
            }
        }
        
        return Result<CompatibilityInfo>.Success(info);
    }
    
    /// <summary>
    /// Updates the database version in the header block.
    /// </summary>
    public async Task<Result> UpdateVersionAsync(DatabaseVersion newVersion)
    {
        try
        {
            _headerContent.FileVersion = EncodeVersion(newVersion);
            
            // Serialize updated header
            var serialized = SerializeHeader(_headerContent);
            if (!serialized.IsSuccess)
                return Result.Failure($"Failed to serialize header: {serialized.Error}");
            
            // Write new header block
            var writeResult = await _blockManager.WriteBlockAsync(
                BlockType.Header,
                serialized.Value,
                blockId: 0);  // Header is always block 0
                
            if (!writeResult.IsSuccess)
                return Result.Failure($"Failed to write header: {writeResult.Error}");
                
            lock (_versionLock)
            {
                _currentVersion = newVersion;
            }
            
            _logger.LogInfo($"Updated database version to {newVersion}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Failed to update version: {ex.Message}");
        }
    }
    
    private async Task<Result<DatabaseVersion>> InitializeNewDatabaseAsync()
    {
        try
        {
            var version = DatabaseVersion.Current;
            
            // Create header content
            _headerContent = new HeaderContent
            {
                FileVersion = EncodeVersion(version),
                FirstMetadataOffset = -1,
                FirstFolderTreeOffset = -1,
                FirstCleanupOffset = -1
            };
            
            // Write header block
            var serialized = SerializeHeader(_headerContent);
            if (!serialized.IsSuccess)
                return Result<DatabaseVersion>.Failure($"Failed to serialize header: {serialized.Error}");
                
            var writeResult = await _blockManager.WriteBlockAsync(
                BlockType.Header,
                serialized.Value,
                blockId: 0);
                
            if (!writeResult.IsSuccess)
                return Result<DatabaseVersion>.Failure($"Failed to write header: {writeResult.Error}");
                
            lock (_versionLock)
            {
                _currentVersion = version;
            }
            
            _logger.LogInfo($"Initialized new database with version {version}");
            return Result<DatabaseVersion>.Success(version);
        }
        catch (Exception ex)
        {
            return Result<DatabaseVersion>.Failure($"Failed to initialize database: {ex.Message}");
        }
    }
    
    private async Task<FeatureCapabilities> DetectCapabilitiesAsync()
    {
        var capabilities = FeatureCapabilities.None;
        
        // Scan blocks to detect features
        var blockLocations = _blockManager.GetBlockLocations();
        var blockTypes = blockLocations.Values.Select(b => b.Type).Distinct();
        
        // Check for compression
        if (blockLocations.Values.Any(b => (b.Flags & 0x0F) != 0))
            capabilities |= FeatureCapabilities.Compression;
            
        // Check for encryption
        if (blockLocations.Values.Any(b => (b.Flags & 0xF0) != 0))
            capabilities |= FeatureCapabilities.Encryption;
            
        // Check for email batching
        if (blockTypes.Contains(BlockType.EmailBatch))
            capabilities |= FeatureCapabilities.EmailBatching;
            
        // Check for envelope blocks
        if (blockTypes.Contains(BlockType.FolderEnvelope))
            capabilities |= FeatureCapabilities.EnvelopeBlocks;
            
        // Check for key management
        if (blockTypes.Contains(BlockType.KeyManager) || blockTypes.Contains(BlockType.KeyExchange))
            capabilities |= FeatureCapabilities.InBandKeyManagement;
            
        // Check for folder hierarchy
        if (blockTypes.Contains(BlockType.Folder))
            capabilities |= FeatureCapabilities.FolderHierarchy;
            
        return capabilities;
    }
    
    private DatabaseVersion ParseVersion(int encodedVersion)
    {
        // Encoding: Major (8 bits) | Minor (8 bits) | Patch (16 bits)
        int major = (encodedVersion >> 24) & 0xFF;
        int minor = (encodedVersion >> 16) & 0xFF;
        int patch = encodedVersion & 0xFFFF;
        
        return new DatabaseVersion(major, minor, patch);
    }
    
    private int EncodeVersion(DatabaseVersion version)
    {
        return (version.Major << 24) | (version.Minor << 16) | version.Patch;
    }
    
    private Result<HeaderContent> DeserializeHeader(byte[] payload)
    {
        try
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);
            
            var header = new HeaderContent
            {
                FileVersion = reader.ReadInt32(),
                FirstMetadataOffset = reader.ReadInt64(),
                FirstFolderTreeOffset = reader.ReadInt64(),
                FirstCleanupOffset = reader.ReadInt64()
            };
            
            return Result<HeaderContent>.Success(header);
        }
        catch (Exception ex)
        {
            return Result<HeaderContent>.Failure($"Deserialization failed: {ex.Message}");
        }
    }
    
    private Result<byte[]> SerializeHeader(HeaderContent header)
    {
        try
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            writer.Write(header.FileVersion);
            writer.Write(header.FirstMetadataOffset);
            writer.Write(header.FirstFolderTreeOffset);
            writer.Write(header.FirstCleanupOffset);
            
            return Result<byte[]>.Success(ms.ToArray());
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure($"Serialization failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Information about database compatibility.
/// </summary>
public class CompatibilityInfo
{
    public DatabaseVersion DatabaseVersion { get; set; }
    public DatabaseVersion ImplementationVersion { get; set; }
    public bool IsCompatible { get; set; }
    public bool CanUpgrade { get; set; }
    public UpgradeType UpgradeType { get; set; }
    public string Message { get; set; }
}
```

### Task 5.1.3: Create Compatibility Matrix
**File**: `EmailDB.Format/Versioning/CompatibilityMatrix.cs`
**Dependencies**: DatabaseVersion
**Description**: Defines version compatibility rules

```csharp
namespace EmailDB.Format.Versioning;

/// <summary>
/// Defines compatibility rules between different database versions.
/// </summary>
public static class CompatibilityMatrix
{
    private static readonly Dictionary<(DatabaseVersion from, DatabaseVersion to), UpgradeStrategy> _upgradeStrategies;
    
    static CompatibilityMatrix()
    {
        _upgradeStrategies = new Dictionary<(DatabaseVersion, DatabaseVersion), UpgradeStrategy>();
        
        // Define upgrade paths
        RegisterUpgradePath(
            from: new DatabaseVersion(1, 0, 0),
            to: new DatabaseVersion(2, 0, 0),
            new UpgradeStrategy
            {
                Type = UpgradeType.Migration,
                RequiresBackup = true,
                EstimatedDuration = TimeSpan.FromMinutes(30),
                MigrationHandlerType = typeof(V1ToV2MigrationHandler),
                Description = "Migrate to batched email storage and envelope blocks"
            });
            
        RegisterUpgradePath(
            from: new DatabaseVersion(1, 1, 0),
            to: new DatabaseVersion(2, 0, 0),
            new UpgradeStrategy
            {
                Type = UpgradeType.Migration,
                RequiresBackup = true,
                EstimatedDuration = TimeSpan.FromMinutes(20),
                MigrationHandlerType = typeof(V1ToV2MigrationHandler),
                Description = "Migrate to batched email storage and envelope blocks"
            });
            
        // Minor version upgrades within v2.x
        RegisterUpgradePath(
            from: new DatabaseVersion(2, 0, 0),
            to: new DatabaseVersion(2, 1, 0),
            new UpgradeStrategy
            {
                Type = UpgradeType.InPlace,
                RequiresBackup = false,
                EstimatedDuration = TimeSpan.FromSeconds(10),
                Description = "Add new indexes for enhanced search"
            });
    }
    
    /// <summary>
    /// Gets the upgrade strategy between two versions.
    /// </summary>
    public static Result<UpgradeStrategy> GetUpgradeStrategy(DatabaseVersion from, DatabaseVersion to)
    {
        // Direct upgrade path
        if (_upgradeStrategies.TryGetValue((from, to), out var strategy))
            return Result<UpgradeStrategy>.Success(strategy);
            
        // Check for multi-step upgrade path
        var path = FindUpgradePath(from, to);
        if (path != null && path.Count > 0)
        {
            var multiStepStrategy = new UpgradeStrategy
            {
                Type = UpgradeType.Migration,
                RequiresBackup = true,
                IsMultiStep = true,
                Steps = path,
                EstimatedDuration = path.Sum(s => s.EstimatedDuration),
                Description = $"Multi-step upgrade from {from} to {to} ({path.Count} steps)"
            };
            return Result<UpgradeStrategy>.Success(multiStepStrategy);
        }
        
        // Check if it's a patch upgrade
        if (from.Major == to.Major && from.Minor == to.Minor && from.Patch < to.Patch)
        {
            var patchStrategy = new UpgradeStrategy
            {
                Type = UpgradeType.InPlace,
                RequiresBackup = false,
                EstimatedDuration = TimeSpan.FromSeconds(1),
                Description = "Patch version update"
            };
            return Result<UpgradeStrategy>.Success(patchStrategy);
        }
        
        return Result<UpgradeStrategy>.Failure($"No upgrade path from {from} to {to}");
    }
    
    /// <summary>
    /// Validates if a version can read blocks from another version.
    /// </summary>
    public static bool CanReadBlockFormat(DatabaseVersion readerVersion, BlockType blockType, int blockVersion)
    {
        // Get expected block version for reader
        if (!readerVersion.BlockFormatVersions.TryGetValue(blockType, out var expectedVersion))
            return false;
            
        // Can read same or older block versions
        return blockVersion <= expectedVersion;
    }
    
    private static void RegisterUpgradePath(DatabaseVersion from, DatabaseVersion to, UpgradeStrategy strategy)
    {
        _upgradeStrategies[(from, to)] = strategy;
    }
    
    private static List<UpgradeStrategy> FindUpgradePath(DatabaseVersion from, DatabaseVersion to)
    {
        // Simple BFS to find upgrade path
        var queue = new Queue<(DatabaseVersion version, List<UpgradeStrategy> path)>();
        var visited = new HashSet<string>();
        
        queue.Enqueue((from, new List<UpgradeStrategy>()));
        visited.Add(from.ToString());
        
        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            
            foreach (var kvp in _upgradeStrategies)
            {
                if (kvp.Key.from == current)
                {
                    var next = kvp.Key.to;
                    if (next == to)
                    {
                        path.Add(kvp.Value);
                        return path;
                    }
                    
                    if (!visited.Contains(next.ToString()))
                    {
                        visited.Add(next.ToString());
                        var newPath = new List<UpgradeStrategy>(path) { kvp.Value };
                        queue.Enqueue((next, newPath));
                    }
                }
            }
        }
        
        return null;
    }
}

/// <summary>
/// Defines how to upgrade between versions.
/// </summary>
public class UpgradeStrategy
{
    public UpgradeType Type { get; set; }
    public bool RequiresBackup { get; set; }
    public TimeSpan EstimatedDuration { get; set; }
    public string Description { get; set; }
    public Type MigrationHandlerType { get; set; }
    public bool IsMultiStep { get; set; }
    public List<UpgradeStrategy> Steps { get; set; }
}
```

## Section 5.2: Upgrade Infrastructure

### Task 5.2.1: Create Upgrade Orchestrator
**File**: `EmailDB.Format/Versioning/UpgradeOrchestrator.cs`
**Dependencies**: FormatVersionManager, CompatibilityMatrix
**Description**: Coordinates database upgrades

```csharp
namespace EmailDB.Format.Versioning;

/// <summary>
/// Orchestrates database upgrades between versions.
/// </summary>
public class UpgradeOrchestrator
{
    private readonly FormatVersionManager _versionManager;
    private readonly RawBlockManager _blockManager;
    private readonly ILogger _logger;
    private readonly string _databasePath;
    
    public UpgradeOrchestrator(
        FormatVersionManager versionManager,
        RawBlockManager blockManager,
        string databasePath,
        ILogger logger = null)
    {
        _versionManager = versionManager ?? throw new ArgumentNullException(nameof(versionManager));
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = logger ?? new ConsoleLogger();
    }
    
    /// <summary>
    /// Checks if an upgrade is needed and returns upgrade information.
    /// </summary>
    public async Task<Result<UpgradeInfo>> CheckUpgradeNeededAsync()
    {
        try
        {
            // Detect current version
            var versionResult = await _versionManager.DetectVersionAsync();
            if (!versionResult.IsSuccess)
                return Result<UpgradeInfo>.Failure($"Failed to detect version: {versionResult.Error}");
                
            var currentVersion = versionResult.Value;
            var targetVersion = DatabaseVersion.Current;
            
            if (currentVersion >= targetVersion)
            {
                return Result<UpgradeInfo>.Success(new UpgradeInfo
                {
                    IsUpgradeNeeded = false,
                    CurrentVersion = currentVersion,
                    TargetVersion = targetVersion,
                    Message = "Database is up to date"
                });
            }
            
            // Get upgrade strategy
            var strategyResult = CompatibilityMatrix.GetUpgradeStrategy(currentVersion, targetVersion);
            if (!strategyResult.IsSuccess)
            {
                return Result<UpgradeInfo>.Success(new UpgradeInfo
                {
                    IsUpgradeNeeded = true,
                    CurrentVersion = currentVersion,
                    TargetVersion = targetVersion,
                    CanUpgrade = false,
                    Message = $"No upgrade path available: {strategyResult.Error}"
                });
            }
            
            var strategy = strategyResult.Value;
            
            return Result<UpgradeInfo>.Success(new UpgradeInfo
            {
                IsUpgradeNeeded = true,
                CanUpgrade = true,
                CurrentVersion = currentVersion,
                TargetVersion = targetVersion,
                Strategy = strategy,
                Message = strategy.Description,
                EstimatedDuration = strategy.EstimatedDuration,
                RequiresBackup = strategy.RequiresBackup
            });
        }
        catch (Exception ex)
        {
            return Result<UpgradeInfo>.Failure($"Failed to check upgrade: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Performs the database upgrade.
    /// </summary>
    public async Task<Result> UpgradeAsync(
        UpgradeInfo upgradeInfo,
        IProgress<UpgradeProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!upgradeInfo.CanUpgrade)
            return Result.Failure("Upgrade is not supported");
            
        try
        {
            _logger.LogInfo($"Starting upgrade from {upgradeInfo.CurrentVersion} to {upgradeInfo.TargetVersion}");
            
            var upgradeProgress = new UpgradeProgress
            {
                CurrentVersion = upgradeInfo.CurrentVersion,
                TargetVersion = upgradeInfo.TargetVersion,
                Phase = "Preparing",
                PercentComplete = 0
            };
            progress?.Report(upgradeProgress);
            
            // Create backup if required
            if (upgradeInfo.RequiresBackup)
            {
                upgradeProgress.Phase = "Creating backup";
                upgradeProgress.PercentComplete = 10;
                progress?.Report(upgradeProgress);
                
                var backupResult = await CreateBackupAsync();
                if (!backupResult.IsSuccess)
                    return Result.Failure($"Backup failed: {backupResult.Error}");
                    
                upgradeProgress.BackupPath = backupResult.Value;
            }
            
            // Execute upgrade based on type
            Result upgradeResult;
            if (upgradeInfo.Strategy.Type == UpgradeType.InPlace)
            {
                upgradeResult = await PerformInPlaceUpgradeAsync(
                    upgradeInfo,
                    progress,
                    cancellationToken);
            }
            else
            {
                upgradeResult = await PerformMigrationUpgradeAsync(
                    upgradeInfo,
                    progress,
                    cancellationToken);
            }
            
            if (!upgradeResult.IsSuccess)
            {
                // Restore from backup if available
                if (!string.IsNullOrEmpty(upgradeProgress.BackupPath))
                {
                    _logger.LogWarning("Upgrade failed, restoring from backup");
                    await RestoreFromBackupAsync(upgradeProgress.BackupPath);
                }
                return upgradeResult;
            }
            
            // Update version in header
            upgradeProgress.Phase = "Finalizing";
            upgradeProgress.PercentComplete = 95;
            progress?.Report(upgradeProgress);
            
            var updateResult = await _versionManager.UpdateVersionAsync(upgradeInfo.TargetVersion);
            if (!updateResult.IsSuccess)
                return updateResult;
                
            upgradeProgress.Phase = "Complete";
            upgradeProgress.PercentComplete = 100;
            progress?.Report(upgradeProgress);
            
            _logger.LogInfo($"Upgrade completed successfully to version {upgradeInfo.TargetVersion}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Upgrade failed: {ex.Message}");
            return Result.Failure($"Upgrade error: {ex.Message}");
        }
    }
    
    private async Task<Result> PerformInPlaceUpgradeAsync(
        UpgradeInfo upgradeInfo,
        IProgress<UpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        var upgradeProgress = new UpgradeProgress
        {
            CurrentVersion = upgradeInfo.CurrentVersion,
            TargetVersion = upgradeInfo.TargetVersion,
            Phase = "Applying in-place updates",
            PercentComplete = 50
        };
        progress?.Report(upgradeProgress);
        
        // In-place upgrades typically only update metadata or add new structures
        // No data migration needed
        
        // Example: Add new index structures
        if (upgradeInfo.TargetVersion.Minor > upgradeInfo.CurrentVersion.Minor)
        {
            // Initialize new features added in minor version
            // This would be version-specific
        }
        
        return Result.Success();
    }
    
    private async Task<Result> PerformMigrationUpgradeAsync(
        UpgradeInfo upgradeInfo,
        IProgress<UpgradeProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var upgradeProgress = new UpgradeProgress
            {
                CurrentVersion = upgradeInfo.CurrentVersion,
                TargetVersion = upgradeInfo.TargetVersion,
                Phase = "Loading migration handler",
                PercentComplete = 20
            };
            progress?.Report(upgradeProgress);
            
            // Create migration handler
            if (upgradeInfo.Strategy.MigrationHandlerType == null)
                return Result.Failure("No migration handler specified");
                
            var handler = Activator.CreateInstance(
                upgradeInfo.Strategy.MigrationHandlerType,
                _blockManager,
                _logger) as IMigrationHandler;
                
            if (handler == null)
                return Result.Failure("Failed to create migration handler");
                
            // Execute migration
            upgradeProgress.Phase = "Migrating data";
            upgradeProgress.PercentComplete = 30;
            progress?.Report(upgradeProgress);
            
            var migrationProgress = new Progress<MigrationProgress>(p =>
            {
                upgradeProgress.Phase = $"Migrating: {p.CurrentOperation}";
                upgradeProgress.PercentComplete = 30 + (int)(p.PercentComplete * 0.6);
                progress?.Report(upgradeProgress);
            });
            
            var migrationResult = await handler.MigrateAsync(
                upgradeInfo.CurrentVersion,
                upgradeInfo.TargetVersion,
                migrationProgress,
                cancellationToken);
                
            return migrationResult;
        }
        catch (Exception ex)
        {
            return Result.Failure($"Migration failed: {ex.Message}");
        }
    }
    
    private async Task<Result<string>> CreateBackupAsync()
    {
        try
        {
            var backupPath = $"{_databasePath}.backup_{DateTime.UtcNow:yyyyMMddHHmmss}";
            
            // Close current file handles
            _blockManager.Dispose();
            
            // Copy database file
            File.Copy(_databasePath, backupPath, overwrite: true);
            
            // Copy any associated files (indexes, etc.)
            var directory = Path.GetDirectoryName(_databasePath);
            var baseName = Path.GetFileNameWithoutExtension(_databasePath);
            
            foreach (var file in Directory.GetFiles(directory, $"{baseName}.*"))
            {
                if (file != _databasePath)
                {
                    var backupFile = file.Replace(_databasePath, backupPath);
                    File.Copy(file, backupFile, overwrite: true);
                }
            }
            
            _logger.LogInfo($"Created backup at: {backupPath}");
            return Result<string>.Success(backupPath);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure($"Backup failed: {ex.Message}");
        }
    }
    
    private async Task RestoreFromBackupAsync(string backupPath)
    {
        try
        {
            _logger.LogInfo($"Restoring from backup: {backupPath}");
            
            // Close current file handles
            _blockManager.Dispose();
            
            // Delete current files
            File.Delete(_databasePath);
            
            // Restore from backup
            File.Move(backupPath, _databasePath);
            
            // Restore associated files
            var directory = Path.GetDirectoryName(backupPath);
            var backupBase = Path.GetFileNameWithoutExtension(backupPath);
            
            foreach (var file in Directory.GetFiles(directory, $"{backupBase}.*"))
            {
                if (file != backupPath)
                {
                    var restoredFile = file.Replace(backupPath, _databasePath);
                    File.Move(file, restoredFile);
                }
            }
            
            _logger.LogInfo("Restore completed");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Restore failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Information about a potential upgrade.
/// </summary>
public class UpgradeInfo
{
    public bool IsUpgradeNeeded { get; set; }
    public bool CanUpgrade { get; set; }
    public DatabaseVersion CurrentVersion { get; set; }
    public DatabaseVersion TargetVersion { get; set; }
    public UpgradeStrategy Strategy { get; set; }
    public string Message { get; set; }
    public TimeSpan EstimatedDuration { get; set; }
    public bool RequiresBackup { get; set; }
}

/// <summary>
/// Progress information for an upgrade operation.
/// </summary>
public class UpgradeProgress
{
    public DatabaseVersion CurrentVersion { get; set; }
    public DatabaseVersion TargetVersion { get; set; }
    public string Phase { get; set; }
    public int PercentComplete { get; set; }
    public string BackupPath { get; set; }
    public string CurrentOperation { get; set; }
}
```

### Task 5.2.2: Create Migration Handler Interface
**File**: `EmailDB.Format/Versioning/IMigrationHandler.cs`
**Dependencies**: DatabaseVersion
**Description**: Interface for version-specific migration handlers

```csharp
namespace EmailDB.Format.Versioning;

/// <summary>
/// Interface for implementing version-specific database migrations.
/// </summary>
public interface IMigrationHandler
{
    /// <summary>
    /// Performs the migration from one version to another.
    /// </summary>
    Task<Result> MigrateAsync(
        DatabaseVersion fromVersion,
        DatabaseVersion toVersion,
        IProgress<MigrationProgress> progress = null,
        CancellationToken cancellationToken = default);
        
    /// <summary>
    /// Validates that the migration can be performed.
    /// </summary>
    Task<Result> ValidateMigrationAsync(
        DatabaseVersion fromVersion,
        DatabaseVersion toVersion);
}

/// <summary>
/// Progress information for a migration operation.
/// </summary>
public class MigrationProgress
{
    public string CurrentOperation { get; set; }
    public int ProcessedItems { get; set; }
    public int TotalItems { get; set; }
    public double PercentComplete => TotalItems > 0 ? (double)ProcessedItems / TotalItems : 0;
    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
}
```

### Task 5.2.3: Create V1 to V2 Migration Handler
**File**: `EmailDB.Format/Versioning/Migrations/V1ToV2MigrationHandler.cs`
**Dependencies**: RawBlockManager, EmailStorageManager
**Description**: Migrates from single emails to batched storage

```csharp
namespace EmailDB.Format.Versioning.Migrations;

/// <summary>
/// Migrates database from v1 (single email blocks) to v2 (batched emails with envelopes).
/// </summary>
public class V1ToV2MigrationHandler : IMigrationHandler
{
    private readonly RawBlockManager _sourceBlockManager;
    private readonly RawBlockManager _targetBlockManager;
    private readonly IBlockContentSerializer _serializer;
    private readonly ILogger _logger;
    
    public V1ToV2MigrationHandler(
        RawBlockManager blockManager,
        ILogger logger = null)
    {
        _sourceBlockManager = blockManager;
        _logger = logger ?? new ConsoleLogger();
        _serializer = new DefaultBlockContentSerializer();
        
        // Create new file for migrated data
        var targetPath = blockManager.FilePath + ".v2";
        _targetBlockManager = new RawBlockManager(targetPath, createIfNotExists: true);
    }
    
    public async Task<Result> ValidateMigrationAsync(
        DatabaseVersion fromVersion,
        DatabaseVersion toVersion)
    {
        // Validate version compatibility
        if (fromVersion.Major != 1 || toVersion.Major != 2)
            return Result.Failure($"This handler only supports v1 to v2 migration");
            
        // Check available disk space
        var sourceSize = new FileInfo(_sourceBlockManager.FilePath).Length;
        var drive = new DriveInfo(Path.GetPathRoot(_sourceBlockManager.FilePath));
        
        if (drive.AvailableFreeSpace < sourceSize * 2)
            return Result.Failure("Insufficient disk space for migration");
            
        return Result.Success();
    }
    
    public async Task<Result> MigrateAsync(
        DatabaseVersion fromVersion,
        DatabaseVersion toVersion,
        IProgress<MigrationProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Starting v1 to v2 migration");
            
            var migrationProgress = new MigrationProgress
            {
                CurrentOperation = "Analyzing source database"
            };
            progress?.Report(migrationProgress);
            
            // Step 1: Analyze source blocks
            var sourceBlocks = await AnalyzeSourceBlocksAsync();
            migrationProgress.TotalItems = sourceBlocks.EmailBlocks.Count;
            
            // Step 2: Initialize target database with v2 header
            await InitializeTargetDatabaseAsync(toVersion);
            
            // Step 3: Create email batches
            migrationProgress.CurrentOperation = "Creating email batches";
            progress?.Report(migrationProgress);
            
            var emailBatcher = new EmailBlockBuilder(
                _targetBlockManager,
                _serializer,
                targetSizeBytes: 50 * 1024 * 1024); // 50MB initial blocks
                
            var folderEnvelopes = new Dictionary<string, List<EmailEnvelope>>();
            var processedEmails = 0;
            
            foreach (var emailBlock in sourceBlocks.EmailBlocks)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result.Failure("Migration cancelled");
                    
                // Read v1 email block
                var blockResult = await _sourceBlockManager.ReadBlockAsync(emailBlock.Offset);
                if (!blockResult.IsSuccess)
                {
                    _logger.LogWarning($"Failed to read email block at {emailBlock.Offset}");
                    continue;
                }
                
                // Deserialize v1 email
                var emailResult = DeserializeV1Email(blockResult.Value);
                if (!emailResult.IsSuccess)
                {
                    _logger.LogWarning($"Failed to deserialize email at {emailBlock.Offset}");
                    continue;
                }
                
                var (message, folderPath) = emailResult.Value;
                
                // Add to batch
                var batchResult = await emailBatcher.AddEmailAsync(
                    message,
                    Encoding.UTF8.GetBytes(message.ToString()));
                    
                if (!batchResult.IsSuccess)
                {
                    _logger.LogWarning($"Failed to batch email: {batchResult.Error}");
                    continue;
                }
                
                var emailId = batchResult.Value;
                
                // Create envelope
                var envelope = new EmailEnvelope
                {
                    CompoundId = emailId.ToCompoundKey(),
                    MessageId = message.MessageId,
                    Subject = message.Subject,
                    From = message.From?.ToString() ?? "",
                    To = message.To?.ToString() ?? "",
                    Date = message.Date.DateTime,
                    Size = blockResult.Value.PayloadLength,
                    HasAttachments = message.Attachments.Any()
                };
                
                // Group by folder
                if (!folderEnvelopes.ContainsKey(folderPath))
                    folderEnvelopes[folderPath] = new List<EmailEnvelope>();
                folderEnvelopes[folderPath].Add(envelope);
                
                processedEmails++;
                migrationProgress.ProcessedItems = processedEmails;
                progress?.Report(migrationProgress);
            }
            
            // Flush remaining emails
            await emailBatcher.FlushAsync();
            
            // Step 4: Create folder and envelope blocks
            migrationProgress.CurrentOperation = "Creating folder structures";
            progress?.Report(migrationProgress);
            
            await CreateFolderStructuresAsync(folderEnvelopes);
            
            // Step 5: Migrate other block types
            migrationProgress.CurrentOperation = "Migrating metadata";
            progress?.Report(migrationProgress);
            
            await MigrateOtherBlocksAsync(sourceBlocks);
            
            // Step 6: Finalize migration
            migrationProgress.CurrentOperation = "Finalizing";
            progress?.Report(migrationProgress);
            
            await FinalizeMigrationAsync();
            
            migrationProgress.CurrentOperation = "Complete";
            migrationProgress.PercentComplete = 100;
            progress?.Report(migrationProgress);
            
            _logger.LogInfo("Migration completed successfully");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Migration failed: {ex.Message}");
            return Result.Failure($"Migration error: {ex.Message}");
        }
    }
    
    private async Task<SourceBlockAnalysis> AnalyzeSourceBlocksAsync()
    {
        var analysis = new SourceBlockAnalysis();
        var blockLocations = _sourceBlockManager.GetBlockLocations();
        
        foreach (var (offset, location) in blockLocations)
        {
            switch (location.Type)
            {
                case BlockType.Email:
                    analysis.EmailBlocks.Add(location);
                    break;
                case BlockType.Metadata:
                    analysis.MetadataBlocks.Add(location);
                    break;
                case BlockType.Folder:
                    analysis.FolderBlocks.Add(location);
                    break;
            }
        }
        
        _logger.LogInfo($"Found {analysis.EmailBlocks.Count} emails to migrate");
        return analysis;
    }
    
    private async Task InitializeTargetDatabaseAsync(DatabaseVersion version)
    {
        // Create v2 header
        var header = new HeaderContent
        {
            FileVersion = (version.Major << 24) | (version.Minor << 16) | version.Patch,
            FirstMetadataOffset = -1,
            FirstFolderTreeOffset = -1,
            FirstCleanupOffset = -1
        };
        
        var serialized = SerializeHeader(header);
        await _targetBlockManager.WriteBlockAsync(
            BlockType.Header,
            serialized,
            blockId: 0);
    }
    
    private Result<(MimeMessage message, string folderPath)> DeserializeV1Email(Block block)
    {
        try
        {
            // V1 format: direct email content
            var message = MimeMessage.Load(new MemoryStream(block.Payload));
            
            // Extract folder from metadata if available
            var folderPath = "Inbox"; // Default
            
            return Result<(MimeMessage, string)>.Success((message, folderPath));
        }
        catch (Exception ex)
        {
            return Result<(MimeMessage, string)>.Failure($"Failed to deserialize v1 email: {ex.Message}");
        }
    }
    
    private async Task CreateFolderStructuresAsync(Dictionary<string, List<EmailEnvelope>> folderEnvelopes)
    {
        foreach (var (folderPath, envelopes) in folderEnvelopes)
        {
            // Create envelope block
            var envelopeBlock = new FolderEnvelopeBlock
            {
                FolderPath = folderPath,
                Version = 1,
                Envelopes = envelopes,
                LastModified = DateTime.UtcNow
            };
            
            var serialized = _serializer.Serialize(envelopeBlock, PayloadEncoding.Protobuf);
            if (!serialized.IsSuccess)
                continue;
                
            var envelopeBlockResult = await _targetBlockManager.WriteBlockAsync(
                BlockType.FolderEnvelope,
                serialized.Value);
                
            if (!envelopeBlockResult.IsSuccess)
                continue;
                
            // Create folder block
            var folder = new FolderContent
            {
                Name = folderPath,
                Version = 1,
                EmailIds = envelopes.Select(e => EmailHashedID.FromCompoundKey(e.CompoundId)).ToList(),
                EnvelopeBlockId = envelopeBlockResult.Value,
                LastModified = DateTime.UtcNow
            };
            
            serialized = _serializer.Serialize(folder, PayloadEncoding.Protobuf);
            if (!serialized.IsSuccess)
                continue;
                
            await _targetBlockManager.WriteBlockAsync(
                BlockType.Folder,
                serialized.Value);
        }
    }
    
    private async Task MigrateOtherBlocksAsync(SourceBlockAnalysis sourceBlocks)
    {
        // Migrate metadata blocks
        foreach (var metadataBlock in sourceBlocks.MetadataBlocks)
        {
            var blockResult = await _sourceBlockManager.ReadBlockAsync(metadataBlock.Offset);
            if (blockResult.IsSuccess)
            {
                await _targetBlockManager.WriteBlockAsync(
                    BlockType.Metadata,
                    blockResult.Value.Payload);
            }
        }
    }
    
    private async Task FinalizeMigrationAsync()
    {
        // Close target file
        _targetBlockManager.Dispose();
        
        // Rename files
        var originalPath = _sourceBlockManager.FilePath;
        var targetPath = _targetBlockManager.FilePath;
        var backupPath = originalPath + ".v1.backup";
        
        // Backup original
        File.Move(originalPath, backupPath);
        
        // Make new file the primary
        File.Move(targetPath, originalPath);
        
        _logger.LogInfo($"Original database backed up to: {backupPath}");
    }
    
    private byte[] SerializeHeader(HeaderContent header)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(header.FileVersion);
        writer.Write(header.FirstMetadataOffset);
        writer.Write(header.FirstFolderTreeOffset);
        writer.Write(header.FirstCleanupOffset);
        
        return ms.ToArray();
    }
    
    private class SourceBlockAnalysis
    {
        public List<BlockLocation> EmailBlocks { get; } = new();
        public List<BlockLocation> MetadataBlocks { get; } = new();
        public List<BlockLocation> FolderBlocks { get; } = new();
    }
}
```

## Section 5.3: Version Checks and Validation

### Task 5.3.1: Update EmailDatabase to Include Version Checks
**File**: `EmailDB.Format/EmailDatabase.cs` (modifications)
**Dependencies**: FormatVersionManager
**Description**: Add version checking to database initialization

```csharp
public partial class EmailDatabase
{
    private FormatVersionManager _versionManager;
    private DatabaseVersion _databaseVersion;
    
    // Modify constructor to include version checking
    public EmailDatabase(string databasePath, bool autoUpgrade = false)
    {
        _blockManager = new RawBlockManager(databasePath);
        _versionManager = new FormatVersionManager(_blockManager);
        
        // Check database version
        var versionCheckResult = CheckAndHandleVersion(autoUpgrade).Result;
        if (!versionCheckResult.IsSuccess)
        {
            throw new InvalidOperationException($"Database version check failed: {versionCheckResult.Error}");
        }
        
        // Continue with initialization...
        InitializeZoneTrees(databasePath);
    }
    
    private async Task<Result> CheckAndHandleVersion(bool autoUpgrade)
    {
        try
        {
            // Detect version
            var versionResult = await _versionManager.DetectVersionAsync();
            if (!versionResult.IsSuccess)
                return Result.Failure($"Failed to detect database version: {versionResult.Error}");
                
            _databaseVersion = versionResult.Value;
            
            // Check compatibility
            var compatibilityResult = _versionManager.CheckCompatibility();
            if (!compatibilityResult.IsSuccess)
                return Result.Failure($"Compatibility check failed: {compatibilityResult.Error}");
                
            var compatibility = compatibilityResult.Value;
            
            if (!compatibility.IsCompatible)
            {
                if (!compatibility.CanUpgrade)
                {
                    return Result.Failure(
                        $"Database version {_databaseVersion} is not compatible with implementation version {DatabaseVersion.Current}. " +
                        $"{compatibility.Message}");
                }
                
                if (!autoUpgrade)
                {
                    return Result.Failure(
                        $"Database version {_databaseVersion} requires upgrade to {DatabaseVersion.Current}. " +
                        $"Set autoUpgrade=true or run upgrade manually.");
                }
                
                // Perform auto upgrade
                var upgradeResult = await PerformAutoUpgradeAsync();
                if (!upgradeResult.IsSuccess)
                    return upgradeResult;
            }
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Version check failed: {ex.Message}");
        }
    }
    
    private async Task<Result> PerformAutoUpgradeAsync()
    {
        var orchestrator = new UpgradeOrchestrator(
            _versionManager,
            _blockManager,
            _blockManager.FilePath);
            
        var upgradeInfoResult = await orchestrator.CheckUpgradeNeededAsync();
        if (!upgradeInfoResult.IsSuccess)
            return Result.Failure($"Failed to check upgrade: {upgradeInfoResult.Error}");
            
        var upgradeInfo = upgradeInfoResult.Value;
        if (!upgradeInfo.CanUpgrade)
            return Result.Failure($"Cannot upgrade: {upgradeInfo.Message}");
            
        // Show upgrade progress
        var progress = new Progress<UpgradeProgress>(p =>
        {
            Console.WriteLine($"Upgrade progress: {p.Phase} ({p.PercentComplete}%)");
        });
        
        return await orchestrator.UpgradeAsync(upgradeInfo, progress);
    }
    
    /// <summary>
    /// Validates that an operation is supported by the current database version.
    /// </summary>
    private Result ValidateFeature(FeatureCapabilities requiredCapabilities)
    {
        return _versionManager.ValidateOperation(
            "Operation",
            requiredCapabilities);
    }
}
```

### Task 5.3.2: Update EmailManager to Validate Features
**File**: `EmailDB.Format/FileManagement/EmailManager.cs` (modifications)
**Dependencies**: FormatVersionManager
**Description**: Add feature validation to operations

```csharp
public partial class EmailManager
{
    private readonly FormatVersionManager _versionManager;
    
    // Add version manager to constructor
    public EmailManager(
        HybridEmailStore hybridStore,
        FolderManager folderManager,
        EmailStorageManager storageManager,
        RawBlockManager blockManager,
        IBlockContentSerializer serializer,
        FormatVersionManager versionManager)
    {
        // ... existing initialization ...
        _versionManager = versionManager ?? throw new ArgumentNullException(nameof(versionManager));
    }
    
    // Update import method to validate features
    public async Task<Result<EmailHashedID>> ImportEMLAsync(string emlContent, string folderPath = "Inbox")
    {
        // Validate required features
        var validationResult = _versionManager.ValidateOperation(
            "ImportEML",
            FeatureCapabilities.EmailBatching | FeatureCapabilities.EnvelopeBlocks);
            
        if (!validationResult.IsSuccess)
            return Result<EmailHashedID>.Failure(validationResult.Error);
            
        // Continue with existing implementation...
        return await ImportEMLAsyncInternal(emlContent, folderPath);
    }
    
    // Add feature validation to search
    public async Task<Result<List<EmailSearchResult>>> SearchAsync(string searchTerm, int maxResults = 50)
    {
        var validationResult = _versionManager.ValidateOperation(
            "Search",
            FeatureCapabilities.FullTextSearch);
            
        if (!validationResult.IsSuccess)
            return Result<List<EmailSearchResult>>.Failure(validationResult.Error);
            
        // Continue with search...
        return await SearchAsyncInternal(searchTerm, maxResults);
    }
}
```

### Task 5.3.3: Create Version Command Line Tool
**File**: `EmailDB.Console/Commands/VersionCommand.cs`
**Dependencies**: FormatVersionManager, UpgradeOrchestrator
**Description**: Command-line tool for version management

```csharp
namespace EmailDB.Console.Commands;

/// <summary>
/// Command for managing database versions.
/// </summary>
public class VersionCommand : ICommand
{
    public string Name => "version";
    public string Description => "Manage database format versions";
    
    public async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length < 2)
        {
            ShowUsage();
            return 1;
        }
        
        var databasePath = args[0];
        var subCommand = args[1];
        
        switch (subCommand.ToLower())
        {
            case "check":
                return await CheckVersion(databasePath);
                
            case "upgrade":
                return await UpgradeDatabase(databasePath);
                
            case "info":
                return await ShowVersionInfo(databasePath);
                
            default:
                ShowUsage();
                return 1;
        }
    }
    
    private async Task<int> CheckVersion(string databasePath)
    {
        Console.WriteLine($"Checking database version: {databasePath}");
        
        using var blockManager = new RawBlockManager(databasePath, createIfNotExists: false, isReadOnly: true);
        var versionManager = new FormatVersionManager(blockManager);
        
        // Detect version
        var versionResult = await versionManager.DetectVersionAsync();
        if (!versionResult.IsSuccess)
        {
            Console.WriteLine($"Error: {versionResult.Error}");
            return 1;
        }
        
        var version = versionResult.Value;
        Console.WriteLine($"Database version: {version}");
        Console.WriteLine($"Implementation version: {DatabaseVersion.Current}");
        
        // Check compatibility
        var compatibilityResult = versionManager.CheckCompatibility();
        if (!compatibilityResult.IsSuccess)
        {
            Console.WriteLine($"Error: {compatibilityResult.Error}");
            return 1;
        }
        
        var compatibility = compatibilityResult.Value;
        
        if (compatibility.IsCompatible)
        {
            Console.WriteLine("✓ Database is compatible");
        }
        else
        {
            Console.WriteLine("✗ Database is not compatible");
            Console.WriteLine($"  {compatibility.Message}");
            
            if (compatibility.CanUpgrade)
            {
                Console.WriteLine($"  Upgrade available: {compatibility.UpgradeType}");
                Console.WriteLine("  Run 'emaildb version upgrade' to upgrade the database");
            }
        }
        
        // Show capabilities
        Console.WriteLine("\nCapabilities:");
        foreach (var capability in Enum.GetValues<FeatureCapabilities>())
        {
            if (capability != FeatureCapabilities.None && 
                (version.Capabilities & capability) == capability)
            {
                Console.WriteLine($"  ✓ {capability}");
            }
        }
        
        return 0;
    }
    
    private async Task<int> UpgradeDatabase(string databasePath)
    {
        Console.WriteLine($"Upgrading database: {databasePath}");
        
        using var blockManager = new RawBlockManager(databasePath);
        var versionManager = new FormatVersionManager(blockManager);
        var orchestrator = new UpgradeOrchestrator(versionManager, blockManager, databasePath);
        
        // Check if upgrade needed
        var upgradeInfoResult = await orchestrator.CheckUpgradeNeededAsync();
        if (!upgradeInfoResult.IsSuccess)
        {
            Console.WriteLine($"Error: {upgradeInfoResult.Error}");
            return 1;
        }
        
        var upgradeInfo = upgradeInfoResult.Value;
        
        if (!upgradeInfo.IsUpgradeNeeded)
        {
            Console.WriteLine("Database is already up to date");
            return 0;
        }
        
        if (!upgradeInfo.CanUpgrade)
        {
            Console.WriteLine($"Cannot upgrade: {upgradeInfo.Message}");
            return 1;
        }
        
        Console.WriteLine($"Upgrade required: {upgradeInfo.CurrentVersion} → {upgradeInfo.TargetVersion}");
        Console.WriteLine($"Upgrade type: {upgradeInfo.Strategy.Type}");
        Console.WriteLine($"Estimated duration: {upgradeInfo.EstimatedDuration}");
        
        if (upgradeInfo.RequiresBackup)
        {
            Console.WriteLine("⚠️  This upgrade requires a backup");
        }
        
        Console.Write("\nProceed with upgrade? (y/n): ");
        var response = Console.ReadLine();
        
        if (response?.ToLower() != "y")
        {
            Console.WriteLine("Upgrade cancelled");
            return 0;
        }
        
        // Perform upgrade with progress
        var progress = new Progress<UpgradeProgress>(p =>
        {
            Console.Write($"\r{p.Phase}: {p.PercentComplete}%");
            if (p.PercentComplete == 100)
                Console.WriteLine();
        });
        
        var upgradeResult = await orchestrator.UpgradeAsync(upgradeInfo, progress);
        
        if (upgradeResult.IsSuccess)
        {
            Console.WriteLine("\n✓ Upgrade completed successfully");
            return 0;
        }
        else
        {
            Console.WriteLine($"\n✗ Upgrade failed: {upgradeResult.Error}");
            return 1;
        }
    }
    
    private async Task<int> ShowVersionInfo(string databasePath)
    {
        Console.WriteLine("EmailDB Version Information");
        Console.WriteLine("===========================");
        
        Console.WriteLine($"\nImplementation Version: {DatabaseVersion.Current}");
        Console.WriteLine($"Minimum Supported: {DatabaseVersion.MinimumSupported}");
        
        Console.WriteLine("\nSupported Capabilities:");
        foreach (var capability in Enum.GetValues<FeatureCapabilities>())
        {
            if (capability != FeatureCapabilities.None && 
                (DatabaseVersion.Current.Capabilities & capability) == capability)
            {
                Console.WriteLine($"  • {capability}");
            }
        }
        
        Console.WriteLine("\nBlock Format Versions:");
        foreach (var kvp in DatabaseVersion.Current.BlockFormatVersions)
        {
            Console.WriteLine($"  • {kvp.Key}: v{kvp.Value}");
        }
        
        if (File.Exists(databasePath))
        {
            Console.WriteLine($"\nDatabase: {databasePath}");
            await CheckVersion(databasePath);
        }
        
        return 0;
    }
    
    private void ShowUsage()
    {
        Console.WriteLine("Usage: emaildb version <database> <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  check    Check database version and compatibility");
        Console.WriteLine("  upgrade  Upgrade database to current version");
        Console.WriteLine("  info     Show version information");
    }
}
```

## Implementation Timeline

### Week 1: Core Version System (Days 1-5)
**Day 1-2: Version Foundation**
- [ ] Task 5.1.1: Create DatabaseVersion class
- [ ] Define FeatureCapabilities enum
- [ ] Implement version comparison logic

**Day 3-4: Version Management**
- [ ] Task 5.1.2: Create FormatVersionManager
- [ ] Implement version detection from header
- [ ] Add capability detection logic

**Day 5: Compatibility Rules**
- [ ] Task 5.1.3: Create CompatibilityMatrix
- [ ] Define upgrade paths
- [ ] Test version compatibility logic

### Week 2: Upgrade Infrastructure (Days 6-10)
**Day 6-7: Upgrade Orchestration**
- [ ] Task 5.2.1: Create UpgradeOrchestrator
- [ ] Implement backup/restore functionality
- [ ] Add progress reporting

**Day 8-9: Migration Framework**
- [ ] Task 5.2.2: Create IMigrationHandler interface
- [ ] Task 5.2.3: Implement V1ToV2MigrationHandler
- [ ] Test migration logic

**Day 10: Integration**
- [ ] Integrate version checks into managers
- [ ] Add feature validation
- [ ] Test upgrade scenarios

### Week 3: Validation and Tools (Days 11-15)
**Day 11-12: Database Integration**
- [ ] Task 5.3.1: Update EmailDatabase with version checks
- [ ] Add auto-upgrade support
- [ ] Test compatibility scenarios

**Day 13: Manager Updates**
- [ ] Task 5.3.2: Add feature validation to EmailManager
- [ ] Update other managers with version checks
- [ ] Ensure graceful degradation

**Day 14: Command Line Tools**
- [ ] Task 5.3.3: Create version command
- [ ] Add upgrade command
- [ ] Create documentation

**Day 15: Testing and Documentation**
- [ ] Comprehensive upgrade testing
- [ ] Performance testing
- [ ] Documentation updates

## Success Criteria

1. **Version Detection**: Accurately detect database version from header
2. **Compatibility Checking**: Correctly identify compatible/incompatible versions
3. **Graceful Upgrades**: Support both in-place and migration upgrades
4. **Feature Validation**: Prevent unsupported operations on older versions
5. **Data Integrity**: No data loss during upgrades
6. **Performance**: Minimal overhead for version checking
7. **User Experience**: Clear error messages and upgrade guidance

## Risk Mitigation

1. **Data Loss**: Always create backup before major version upgrades
2. **Corruption**: Validate data integrity after upgrades
3. **Compatibility**: Extensive testing of upgrade paths
4. **Performance**: Cache version information to avoid repeated checks
5. **Rollback**: Support restoration from backup if upgrade fails

## Integration Points

### With Existing Components
1. **RawBlockManager**: Read/write header blocks
2. **EmailDatabase**: Version checking on initialization
3. **EmailManager**: Feature validation for operations
4. **Block Types**: Version-specific block formats

### With Future Features
1. **Multi-version Support**: Read older formats without migration
2. **Gradual Migration**: Migrate data on-demand
3. **Version-specific Optimizations**: Enable features based on version
4. **Backward Compatibility**: Write older formats when needed