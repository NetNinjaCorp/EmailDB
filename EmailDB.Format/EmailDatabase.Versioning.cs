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
    private MigrationManager _migrationManager;
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
            _migrationManager = new MigrationManager(_blockManager, _versionManager);
            
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
    
    /// <summary>
    /// Plans a migration to a target version.
    /// </summary>
    public async Task<Result<MigrationPlan>> PlanMigrationAsync(DatabaseVersion targetVersion)
    {
        if (_migrationManager == null)
        {
            return Result<MigrationPlan>.Failure("Migration manager not initialized");
        }
        
        return await _migrationManager.CanMigrateAsync(_databaseVersion, targetVersion);
    }
    
    /// <summary>
    /// Performs a migration to a target version.
    /// </summary>
    public async Task<Result<MigrationResult>> MigrateAsync(DatabaseVersion targetVersion, IProgress<MigrationProgress> progress = null)
    {
        if (_migrationManager == null)
        {
            return Result<MigrationResult>.Failure("Migration manager not initialized");
        }
        
        var result = await _migrationManager.MigrateAsync(targetVersion, progress);
        
        if (result.IsSuccess)
        {
            // Update cached version after successful migration
            _databaseVersion = targetVersion;
        }
        
        return result;
    }
    
    /// <summary>
    /// Checks if the database can be migrated to the current implementation version.
    /// </summary>
    public async Task<Result<bool>> CanUpgradeToCurrentAsync()
    {
        var planResult = await PlanMigrationAsync(DatabaseVersion.Current);
        if (!planResult.IsSuccess)
        {
            return Result<bool>.Failure(planResult.Error);
        }
        
        return Result<bool>.Success(planResult.Value.IsPossible);
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