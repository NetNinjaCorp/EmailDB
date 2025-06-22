using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;

namespace EmailDB.Format.Versioning;

/// <summary>
/// Manages database migrations and upgrades between versions.
/// </summary>
public class MigrationManager
{
    private readonly RawBlockManager _blockManager;
    private readonly FormatVersionManager _versionManager;
    private readonly Dictionary<(int fromMajor, int toMajor), IMigrationStep> _migrationSteps;

    public MigrationManager(RawBlockManager blockManager, FormatVersionManager versionManager)
    {
        _blockManager = blockManager ?? throw new ArgumentNullException(nameof(blockManager));
        _versionManager = versionManager ?? throw new ArgumentNullException(nameof(versionManager));
        _migrationSteps = new Dictionary<(int, int), IMigrationStep>();
        
        RegisterMigrationSteps();
    }

    /// <summary>
    /// Checks if migration is possible from current version to target version.
    /// </summary>
    public async Task<Result<MigrationPlan>> CanMigrateAsync(DatabaseVersion from, DatabaseVersion to)
    {
        try
        {
            var plan = new MigrationPlan
            {
                FromVersion = from,
                ToVersion = to,
                IsPossible = false,
                EstimatedDurationMinutes = 0,
                RequiredDiskSpaceBytes = 0,
                Steps = new List<MigrationStepInfo>()
            };

            // Cannot downgrade
            if (to < from)
            {
                plan.Reason = "Downgrade not supported";
                return Result<MigrationPlan>.Success(plan);
            }

            // Same version = no migration needed
            if (to == from)
            {
                plan.IsPossible = true;
                plan.Reason = "No migration needed - same version";
                return Result<MigrationPlan>.Success(plan);
            }

            // In-place upgrades (minor/patch)
            if (to.Major == from.Major)
            {
                plan.IsPossible = true;
                plan.MigrationType = MigrationType.InPlace;
                plan.Reason = "In-place upgrade for same major version";
                plan.EstimatedDurationMinutes = 1;
                
                plan.Steps.Add(new MigrationStepInfo
                {
                    StepName = "Update version metadata",
                    Description = "Update header block with new version information",
                    EstimatedDurationMinutes = 1,
                    IsReversible = true
                });
                
                return Result<MigrationPlan>.Success(plan);
            }

            // Major version upgrades
            if (to.Major == from.Major + 1)
            {
                // Check if we have a migration step for this major version jump
                if (_migrationSteps.ContainsKey((from.Major, to.Major)))
                {
                    plan.IsPossible = true;
                    plan.MigrationType = MigrationType.Migration;
                    plan.Reason = "Migration available for major version upgrade";
                    
                    // Get migration details from registered step
                    var migrationStep = _migrationSteps[(from.Major, to.Major)];
                    var stepPlan = await migrationStep.PlanMigrationAsync(from, to);
                    
                    plan.EstimatedDurationMinutes = stepPlan.EstimatedDurationMinutes;
                    plan.RequiredDiskSpaceBytes = stepPlan.RequiredDiskSpaceBytes;
                    plan.Steps = stepPlan.Steps;
                }
                else
                {
                    plan.Reason = $"No migration path available from v{from.Major} to v{to.Major}";
                }
                
                return Result<MigrationPlan>.Success(plan);
            }

            // Multiple major version jumps not supported
            plan.Reason = $"Cannot skip major versions: v{from.Major} -> v{to.Major}";
            return Result<MigrationPlan>.Success(plan);
        }
        catch (Exception ex)
        {
            return Result<MigrationPlan>.Failure($"Migration planning failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs the migration from current version to target version.
    /// </summary>
    public async Task<Result<MigrationResult>> MigrateAsync(DatabaseVersion to, IProgress<MigrationProgress> progress = null)
    {
        try
        {
            var currentVersion = _versionManager.CurrentVersion;
            if (currentVersion == null)
            {
                return Result<MigrationResult>.Failure("Current database version not detected");
            }

            var planResult = await CanMigrateAsync(currentVersion, to);
            if (!planResult.IsSuccess || !planResult.Value.IsPossible)
            {
                return Result<MigrationResult>.Failure(
                    planResult.IsSuccess ? planResult.Value.Reason : planResult.Error);
            }

            var plan = planResult.Value;
            var result = new MigrationResult
            {
                FromVersion = currentVersion,
                ToVersion = to,
                StartTime = DateTime.UtcNow,
                Success = false
            };

            progress?.Report(new MigrationProgress 
            { 
                CurrentStep = "Starting migration",
                ProgressPercentage = 0,
                EstimatedTimeRemaining = TimeSpan.FromMinutes(plan.EstimatedDurationMinutes)
            });

            try
            {
                if (plan.MigrationType == MigrationType.InPlace)
                {
                    // Simple in-place upgrade
                    await PerformInPlaceUpgradeAsync(currentVersion, to, progress);
                }
                else if (plan.MigrationType == MigrationType.Migration)
                {
                    // Full migration
                    await PerformMigrationAsync(currentVersion, to, plan, progress);
                }

                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;

                progress?.Report(new MigrationProgress
                {
                    CurrentStep = "Migration completed successfully",
                    ProgressPercentage = 100,
                    EstimatedTimeRemaining = TimeSpan.Zero
                });

                return Result<MigrationResult>.Success(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.ErrorMessage = ex.Message;

                return Result<MigrationResult>.Failure($"Migration failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return Result<MigrationResult>.Failure($"Migration error: {ex.Message}");
        }
    }

    private async Task PerformInPlaceUpgradeAsync(
        DatabaseVersion from, 
        DatabaseVersion to, 
        IProgress<MigrationProgress> progress)
    {
        progress?.Report(new MigrationProgress
        {
            CurrentStep = "Updating version metadata",
            ProgressPercentage = 50
        });

        // Update header with new version information
        await _versionManager.UpdateHeaderAsync(header =>
        {
            header.FileVersion = EncodeVersion(to);
            header.Capabilities = to.Capabilities;
            header.BlockFormatVersions = new Dictionary<Models.BlockType, int>(to.BlockFormatVersions);
            header.Metadata["upgraded_from"] = from.ToString();
            header.Metadata["upgraded_at"] = DateTime.UtcNow.ToString("O");
        });
    }

    private async Task PerformMigrationAsync(
        DatabaseVersion from,
        DatabaseVersion to,
        MigrationPlan plan,
        IProgress<MigrationProgress> progress)
    {
        var migrationStep = _migrationSteps[(from.Major, to.Major)];
        await migrationStep.ExecuteMigrationAsync(from, to, progress);
    }

    private void RegisterMigrationSteps()
    {
        // Register migration from v1 to v2
        _migrationSteps[(1, 2)] = new V1ToV2MigrationStep(_blockManager);
        
        // Future migrations can be registered here
        // _migrationSteps[(2, 3)] = new V2ToV3MigrationStep(_blockManager);
    }

    private int EncodeVersion(DatabaseVersion version)
    {
        return (version.Major << 24) | (version.Minor << 16) | version.Patch;
    }
}

/// <summary>
/// Interface for migration steps between major versions.
/// </summary>
public interface IMigrationStep
{
    Task<MigrationStepPlan> PlanMigrationAsync(DatabaseVersion from, DatabaseVersion to);
    Task ExecuteMigrationAsync(DatabaseVersion from, DatabaseVersion to, IProgress<MigrationProgress> progress);
}

/// <summary>
/// Migration from v1.x.x to v2.x.x.
/// </summary>
public class V1ToV2MigrationStep : IMigrationStep
{
    private readonly RawBlockManager _blockManager;

    public V1ToV2MigrationStep(RawBlockManager blockManager)
    {
        _blockManager = blockManager;
    }

    public async Task<MigrationStepPlan> PlanMigrationAsync(DatabaseVersion from, DatabaseVersion to)
    {
        var blockCount = _blockManager.GetBlockLocations().Count;
        
        return new MigrationStepPlan
        {
            EstimatedDurationMinutes = Math.Max(1, blockCount / 1000), // Rough estimate
            RequiredDiskSpaceBytes = blockCount * 1024, // Extra space for migration
            Steps = new List<MigrationStepInfo>
            {
                new MigrationStepInfo
                {
                    StepName = "Analyze existing blocks",
                    Description = "Scan existing blocks for compatibility",
                    EstimatedDurationMinutes = 1,
                    IsReversible = true
                },
                new MigrationStepInfo
                {
                    StepName = "Update block formats",
                    Description = "Convert v1 blocks to v2 format",
                    EstimatedDurationMinutes = Math.Max(1, blockCount / 1000),
                    IsReversible = false
                },
                new MigrationStepInfo
                {
                    StepName = "Create v2 indexes",
                    Description = "Build new indexes for v2 features",
                    EstimatedDurationMinutes = 1,
                    IsReversible = true
                }
            }
        };
    }

    public async Task ExecuteMigrationAsync(DatabaseVersion from, DatabaseVersion to, IProgress<MigrationProgress> progress)
    {
        progress?.Report(new MigrationProgress
        {
            CurrentStep = "Analyzing existing blocks",
            ProgressPercentage = 10
        });

        // Step 1: Analyze existing blocks
        var blockLocations = _blockManager.GetBlockLocations();
        
        progress?.Report(new MigrationProgress
        {
            CurrentStep = "Updating block formats",
            ProgressPercentage = 30
        });

        // Step 2: Update block formats (placeholder - in real implementation 
        // this would convert actual block structures)
        await Task.Delay(100); // Simulate work
        
        progress?.Report(new MigrationProgress
        {
            CurrentStep = "Creating v2 indexes",
            ProgressPercentage = 80
        });

        // Step 3: Create v2 indexes (placeholder)
        await Task.Delay(100); // Simulate work
    }
}