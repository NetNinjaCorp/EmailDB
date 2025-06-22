using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.Format.Versioning;

/// <summary>
/// Manages database format versions and compatibility.
/// </summary>
public class FormatVersionManager
{
    private readonly RawBlockManager _blockManager;
    
    // Cached version information
    private DatabaseVersion _currentVersion;
    private HeaderContent _headerContent;
    private readonly object _versionLock = new();
    private bool _versionDetected = false;
    
    public FormatVersionManager(RawBlockManager blockManager)
    {
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
    }
    
    public DatabaseVersion CurrentVersion 
    { 
        get 
        { 
            lock (_versionLock) 
            { 
                return _currentVersion; 
            } 
        } 
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
                if (_versionDetected && _currentVersion != null)
                    return Result<DatabaseVersion>.Success(_currentVersion);
            }
            
            // Try to read header block at position 0
            var headerResult = await ReadHeaderBlockAsync();
            if (!headerResult.IsSuccess)
            {
                // No header block - check if database is empty
                var blockLocations = _blockManager.GetBlockLocations();
                if (blockLocations.Count == 0)
                {
                    // Empty database - initialize with current version
                    return await InitializeNewDatabaseAsync();
                }
                else
                {
                    // Has blocks but no header - possibly old format or corrupted
                    // Default to v1.0.0 for existing data
                    var fallbackVersion = new DatabaseVersion(1, 0, 0, FeatureCapabilities.V1Capabilities);
                    
                    lock (_versionLock)
                    {
                        _currentVersion = fallbackVersion;
                        _versionDetected = true;
                    }
                    
                    return Result<DatabaseVersion>.Success(fallbackVersion);
                }
            }
            
            var header = headerResult.Value;
            
            // Parse version from header
            var version = ParseVersionFromHeader(header);
            if (version == null)
            {
                return Result<DatabaseVersion>.Failure("Invalid version in header block");
            }
            
            // Detect capabilities from existing blocks
            var capabilities = await DetectCapabilitiesAsync();
            var detectedVersion = new DatabaseVersion(version.Major, version.Minor, version.Patch, capabilities);
            
            lock (_versionLock)
            {
                _currentVersion = detectedVersion;
                _headerContent = header;
                _versionDetected = true;
            }
            
            return Result<DatabaseVersion>.Success(detectedVersion);
        }
        catch (Exception ex)
        {
            return Result<DatabaseVersion>.Failure($"Version detection failed: {ex.Message}");
        }
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
        else
        {
            info.Message = "Database is compatible with current implementation";
        }
        
        return Result<CompatibilityInfo>.Success(info);
    }
    
    private async Task<Result<HeaderContent>> ReadHeaderBlockAsync()
    {
        try
        {
            // Look for metadata block (acts as header for now)
            var blockLocations = _blockManager.GetBlockLocations();
            
            // Find a metadata block by reading blocks
            foreach (var (offset, location) in blockLocations.Take(5)) // Check first few blocks
            {
                try
                {
                    var blockResult = await _blockManager.ReadBlockAsync(offset);
                    if (blockResult.IsSuccess && blockResult.Value.Type == BlockType.Metadata)
                    {
                        // Found a metadata block - treat as header
                        var headerContent = new HeaderContent
                        {
                            FileVersion = EncodeVersion(DatabaseVersion.Current),
                            FirstMetadataOffset = offset,
                            FirstFolderTreeOffset = -1,
                            FirstCleanupOffset = -1
                        };
                        
                        return Result<HeaderContent>.Success(headerContent);
                    }
                }
                catch
                {
                    // Skip this block
                    continue;
                }
            }
            
            return Result<HeaderContent>.Failure("No header block found");
        }
        catch (Exception ex)
        {
            return Result<HeaderContent>.Failure($"Error reading header: {ex.Message}");
        }
    }
    
    private async Task<Result<DatabaseVersion>> InitializeNewDatabaseAsync()
    {
        try
        {
            var version = DatabaseVersion.Current;
            
            lock (_versionLock)
            {
                _currentVersion = version;
                _versionDetected = true;
            }
            
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
        
        try
        {
            var blockLocations = _blockManager.GetBlockLocations();
            var blockTypes = new HashSet<BlockType>();
            
            // Read a few blocks to determine their types
            int sampledBlocks = 0;
            const int maxSampleBlocks = 10;
            
            foreach (var (offset, location) in blockLocations)
            {
                if (sampledBlocks >= maxSampleBlocks) break;
                
                try
                {
                    var blockResult = await _blockManager.ReadBlockAsync(offset);
                    if (blockResult.IsSuccess)
                    {
                        blockTypes.Add(blockResult.Value.Type);
                        sampledBlocks++;
                    }
                }
                catch
                {
                    // Skip this block if we can't read it
                    continue;
                }
            }
            
            // Always include basic capabilities
            capabilities |= FeatureCapabilities.FolderHierarchy;
            
            // Check for email batching
            if (blockTypes.Contains(BlockType.EmailBatch))
            {
                capabilities |= FeatureCapabilities.EmailBatching;
            }
            
            // Check for envelope blocks
            if (blockTypes.Contains(BlockType.FolderEnvelope))
            {
                capabilities |= FeatureCapabilities.EnvelopeBlocks;
            }
            
            // Check for key management
            if (blockTypes.Contains(BlockType.KeyManager) || blockTypes.Contains(BlockType.KeyExchange))
            {
                capabilities |= FeatureCapabilities.InBandKeyManagement;
            }
            
            // Check for folder hierarchy
            if (blockTypes.Contains(BlockType.Folder))
            {
                capabilities |= FeatureCapabilities.FolderHierarchy;
            }
            
            // Infer full-text search from having emails
            if (blockTypes.Any(t => t == BlockType.EmailBatch || t == BlockType.Folder))
            {
                capabilities |= FeatureCapabilities.FullTextSearch;
            }
            
            return capabilities;
        }
        catch (Exception)
        {
            return FeatureCapabilities.None;
        }
    }
    
    private DatabaseVersion ParseVersionFromHeader(HeaderContent header)
    {
        try
        {
            // Version encoding: Major (8 bits) | Minor (8 bits) | Patch (16 bits)
            int major = (header.FileVersion >> 24) & 0xFF;
            int minor = (header.FileVersion >> 16) & 0xFF;
            int patch = header.FileVersion & 0xFFFF;
            
            if (major < 0 || minor < 0 || patch < 0)
            {
                return null;
            }
            
            return new DatabaseVersion(major, minor, patch);
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    private int EncodeVersion(DatabaseVersion version)
    {
        return (version.Major << 24) | (version.Minor << 16) | version.Patch;
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