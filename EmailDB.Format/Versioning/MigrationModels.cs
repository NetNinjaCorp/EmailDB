using System;
using System.Collections.Generic;

namespace EmailDB.Format.Versioning;

/// <summary>
/// Migration plan containing all information about a proposed migration.
/// </summary>
public class MigrationPlan
{
    public DatabaseVersion FromVersion { get; set; }
    public DatabaseVersion ToVersion { get; set; }
    public bool IsPossible { get; set; }
    public string Reason { get; set; } = "";
    public MigrationType MigrationType { get; set; }
    public int EstimatedDurationMinutes { get; set; }
    public long RequiredDiskSpaceBytes { get; set; }
    public List<MigrationStepInfo> Steps { get; set; } = new();
}

/// <summary>
/// Plan for a specific migration step.
/// </summary>
public class MigrationStepPlan
{
    public int EstimatedDurationMinutes { get; set; }
    public long RequiredDiskSpaceBytes { get; set; }
    public List<MigrationStepInfo> Steps { get; set; } = new();
}

/// <summary>
/// Information about a single migration step.
/// </summary>
public class MigrationStepInfo
{
    public string StepName { get; set; } = "";
    public string Description { get; set; } = "";
    public int EstimatedDurationMinutes { get; set; }
    public bool IsReversible { get; set; }
}

/// <summary>
/// Result of a migration operation.
/// </summary>
public class MigrationResult
{
    public DatabaseVersion FromVersion { get; set; }
    public DatabaseVersion ToVersion { get; set; }
    public bool Success { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string ErrorMessage { get; set; } = "";
}

/// <summary>
/// Progress information for migration operations.
/// </summary>
public class MigrationProgress
{
    public string CurrentStep { get; set; } = "";
    public double ProgressPercentage { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
    public long ProcessedBytes { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>
/// Types of migrations.
/// </summary>
public enum MigrationType
{
    /// <summary>
    /// In-place upgrade that doesn't require data restructuring.
    /// </summary>
    InPlace,
    
    /// <summary>
    /// Full migration that may require data restructuring.
    /// </summary>
    Migration,
    
    /// <summary>
    /// Migration not supported.
    /// </summary>
    NotSupported
}