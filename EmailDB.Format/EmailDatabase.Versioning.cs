using System;
using System.Threading.Tasks;
using EmailDB.Format.Versioning;

namespace EmailDB.Format;

/// <summary>
/// Versioning functionality for EmailDatabase.
/// </summary>
public partial class EmailDatabase
{
    private FormatVersionManager _versionManager;
    private DatabaseVersion _databaseVersion;
    
    /// <summary>
    /// Gets the current database version.
    /// </summary>
    public DatabaseVersion DatabaseVersion => _databaseVersion ?? DatabaseVersion.Current;
    
    /// <summary>
    /// Initializes versioning system (called from constructor).
    /// </summary>
    private async Task InitializeVersioningAsync()
    {
        try
        {
            _versionManager = new FormatVersionManager(_blockManager);
            
            var versionResult = await _versionManager.DetectVersionAsync();
            if (versionResult.IsSuccess)
            {
                _databaseVersion = versionResult.Value;
                Console.WriteLine($"📋 Database version: {_databaseVersion}");
            }
            else
            {
                // Default to current version for new databases
                _databaseVersion = DatabaseVersion.Current;
                Console.WriteLine($"📋 New database, using version: {_databaseVersion}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Version detection failed: {ex.Message}");
            _databaseVersion = DatabaseVersion.Current;
        }
    }
    
    /// <summary>
    /// Gets version compatibility information.
    /// </summary>
    public async Task<VersionCompatibilityInfo> GetVersionCompatibilityAsync()
    {
        if (_versionManager == null)
            return new VersionCompatibilityInfo
            {
                DatabaseVersion = DatabaseVersion.Current,
                ImplementationVersion = DatabaseVersion.Current,
                IsCompatible = true,
                Message = "Version manager not initialized"
            };
            
        try
        {
            var compatibilityResult = _versionManager.CheckCompatibility();
            if (compatibilityResult.IsSuccess)
            {
                var compatibility = compatibilityResult.Value;
                return new VersionCompatibilityInfo
                {
                    DatabaseVersion = compatibility.DatabaseVersion,
                    ImplementationVersion = compatibility.ImplementationVersion,
                    IsCompatible = compatibility.IsCompatible,
                    CanUpgrade = compatibility.CanUpgrade,
                    UpgradeType = compatibility.UpgradeType,
                    Message = compatibility.Message
                };
            }
            else
            {
                return new VersionCompatibilityInfo
                {
                    DatabaseVersion = _databaseVersion,
                    ImplementationVersion = DatabaseVersion.Current,
                    IsCompatible = false,
                    Message = compatibilityResult.Error
                };
            }
        }
        catch (Exception ex)
        {
            return new VersionCompatibilityInfo
            {
                DatabaseVersion = _databaseVersion,
                ImplementationVersion = DatabaseVersion.Current,
                IsCompatible = false,
                Message = $"Compatibility check failed: {ex.Message}"
            };
        }
    }
}

/// <summary>
/// Version compatibility information for the EmailDatabase.
/// </summary>
public class VersionCompatibilityInfo
{
    public DatabaseVersion DatabaseVersion { get; set; }
    public DatabaseVersion ImplementationVersion { get; set; }
    public bool IsCompatible { get; set; }
    public bool CanUpgrade { get; set; }
    public UpgradeType UpgradeType { get; set; }
    public string Message { get; set; }
}