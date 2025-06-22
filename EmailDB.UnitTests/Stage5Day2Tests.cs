using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format;
using EmailDB.Format.Versioning;
using EmailDB.Format.FileManagement;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for Stage 5 Day 2: Enhanced version detection and EML import.
/// </summary>
[Trait("Category", "Stage5")]
public class Stage5Day2Tests : IDisposable
{
    private readonly string _testFile;
    
    public Stage5Day2Tests()
    {
        _testFile = Path.GetTempFileName();
    }
    
    [Fact]
    public async Task FormatVersionManager_CreateHeaderBlock_CreatesValidHeader()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Create a new RawBlockManager for testing
        using var blockManager = new RawBlockManager(_testFile + "_header_test");
        var versionManager = new FormatVersionManager(blockManager);
        
        var headerResult = await versionManager.CreateHeaderBlockAsync();
        
        Assert.True(headerResult.IsSuccess);
        Assert.True(headerResult.Value.Position >= 0); // Valid block position
        
        // Cleanup
        blockManager.Dispose();
        File.Delete(_testFile + "_header_test");
    }
    
    [Fact]
    public async Task EmailDatabase_ImportEMLWithVersionCheck_ChecksCompatibility()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var testEml = @"From: test@example.com
To: recipient@example.com
Subject: Test Email
Date: Mon, 1 Jan 2024 12:00:00 +0000

This is a test email body.";

        var result = await emailDB.ImportEMLWithVersionCheckAsync(testEml, "test.eml");
        
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
    }
    
    [Fact]
    public async Task EmailDatabase_IsOperationSupported_ChecksCapabilities()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Check various operations
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.BasicEMLImport));
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.FullTextSearch));
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.EmailBatching));
        
        // These should be supported in v2.0.0
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.Compression));
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.BlockSuperseding));
    }
    
    [Fact]
    public async Task EmailDatabase_GetDetailedVersionInfo_ReturnsCompleteInfo()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var versionInfo = await emailDB.GetDetailedVersionInfoAsync();
        
        Assert.NotNull(versionInfo);
        Assert.NotNull(versionInfo.DatabaseVersion);
        Assert.NotNull(versionInfo.ImplementationVersion);
        Assert.True(versionInfo.IsCompatible);
        Assert.NotEmpty(versionInfo.SupportedOperations);
        Assert.NotEmpty(versionInfo.BlockFormatVersions);
    }
    
    [Fact]
    public async Task MigrationManager_CanMigrate_SameVersion_ReturnsPossible()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var currentVersion = emailDB.DatabaseVersion;
        var planResult = await emailDB.PlanMigrationAsync(currentVersion);
        
        Assert.True(planResult.IsSuccess);
        Assert.True(planResult.Value.IsPossible);
        Assert.Contains("same version", planResult.Value.Reason.ToLowerInvariant());
    }
    
    [Fact]
    public async Task MigrationManager_CanMigrate_MinorUpgrade_ReturnsPossible()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var currentVersion = emailDB.DatabaseVersion;
        var targetVersion = new DatabaseVersion(currentVersion.Major, currentVersion.Minor + 1, 0);
        
        var planResult = await emailDB.PlanMigrationAsync(targetVersion);
        
        Assert.True(planResult.IsSuccess);
        Assert.True(planResult.Value.IsPossible);
        Assert.Equal(MigrationType.InPlace, planResult.Value.MigrationType);
    }
    
    [Fact]
    public async Task MigrationManager_CanMigrate_MajorUpgrade_ChecksAvailability()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var currentVersion = emailDB.DatabaseVersion;
        var targetVersion = new DatabaseVersion(currentVersion.Major + 1, 0, 0);
        
        var planResult = await emailDB.PlanMigrationAsync(targetVersion);
        
        Assert.True(planResult.IsSuccess);
        // Result depends on available migration steps
        Assert.NotNull(planResult.Value.Reason);
    }
    
    [Fact]
    public async Task MigrationManager_CanMigrate_Downgrade_ReturnsNotPossible()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var currentVersion = emailDB.DatabaseVersion;
        var targetVersion = new DatabaseVersion(Math.Max(1, currentVersion.Major - 1), 0, 0);
        
        var planResult = await emailDB.PlanMigrationAsync(targetVersion);
        
        Assert.True(planResult.IsSuccess);
        Assert.False(planResult.Value.IsPossible);
        Assert.Contains("downgrade", planResult.Value.Reason.ToLowerInvariant());
    }
    
    [Fact]
    public async Task EmailDatabase_CanUpgradeToCurrentAsync_ReturnsCorrectResult()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var canUpgradeResult = await emailDB.CanUpgradeToCurrentAsync();
        
        Assert.True(canUpgradeResult.IsSuccess);
        // Should be true since we're already at current version or can upgrade to it
        Assert.True(canUpgradeResult.Value);
    }
    
    [Fact]
    public async Task EmailDatabase_ImportEMLFileWithVersionCheck_HandlesFileNotFound()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var result = await emailDB.ImportEMLFileWithVersionCheckAsync("nonexistent.eml");
        
        Assert.False(result.IsSuccess);
        Assert.Contains("File not found", result.Error);
    }
    
    [Fact]
    public async Task EmailDatabase_ImportEMLBatchWithVersionCheck_ReportsProgress()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var emails = new[]
        {
            ("test1.eml", @"From: test1@example.com
Subject: Test 1
Date: Mon, 1 Jan 2024 12:00:00 +0000

Test body 1"),
            ("test2.eml", @"From: test2@example.com
Subject: Test 2
Date: Mon, 1 Jan 2024 12:01:00 +0000

Test body 2")
        };
        
        BatchImportProgress lastProgress = null;
        var progress = new Progress<BatchImportProgress>(p => lastProgress = p);
        
        var result = await emailDB.ImportEMLBatchWithVersionCheckAsync(emails, progress);
        
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.SuccessCount);
        Assert.Equal(0, result.Value.ErrorCount);
        Assert.NotNull(lastProgress);
        Assert.Equal(100, lastProgress.ProgressPercentage);
    }
    
    [Fact]
    public async Task VersionAwareBatchImportResult_IncludesVersionInfo()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var emails = new[]
        {
            ("test.eml", @"From: test@example.com
Subject: Test
Date: Mon, 1 Jan 2024 12:00:00 +0000

Test body")
        };
        
        var result = await emailDB.ImportEMLBatchWithVersionCheckAsync(emails);
        
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.DatabaseVersion);
        Assert.True(result.Value.ImportStartTime <= result.Value.ImportEndTime);
        Assert.True(result.Value.TotalDuration >= TimeSpan.Zero);
    }
    
    public void Dispose()
    {
        try
        {
            if (File.Exists(_testFile))
            {
                File.Delete(_testFile);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

/// <summary>
/// Tests for migration functionality.
/// </summary>
[Trait("Category", "Stage5")]
public class Stage5MigrationTests : IDisposable
{
    private readonly string _testFile;
    
    public Stage5MigrationTests()
    {
        _testFile = Path.GetTempFileName();
    }
    
    [Fact]
    public async Task V1ToV2MigrationStep_PlanMigration_ReturnsValidPlan()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Create a new RawBlockManager for testing
        using var blockManager = new RawBlockManager(_testFile + "_migration_test");
        var migrationStep = new V1ToV2MigrationStep(blockManager);
        
        var from = new DatabaseVersion(1, 0, 0);
        var to = new DatabaseVersion(2, 0, 0);
        
        var plan = await migrationStep.PlanMigrationAsync(from, to);
        
        Assert.NotNull(plan);
        Assert.True(plan.EstimatedDurationMinutes >= 1);
        Assert.True(plan.RequiredDiskSpaceBytes >= 0);
        Assert.NotEmpty(plan.Steps);
        
        // Cleanup
        blockManager.Dispose();
        File.Delete(_testFile + "_migration_test");
    }
    
    [Fact]
    public async Task MigrationManager_InPlaceUpgrade_UpdatesVersionCorrectly()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var currentVersion = emailDB.DatabaseVersion;
        var targetVersion = new DatabaseVersion(currentVersion.Major, currentVersion.Minor, currentVersion.Patch + 1);
        
        var result = await emailDB.MigrateAsync(targetVersion);
        
        Assert.True(result.IsSuccess);
        Assert.Equal(targetVersion, result.Value.ToVersion);
        Assert.True(result.Value.Success);
    }
    
    public void Dispose()
    {
        try
        {
            if (File.Exists(_testFile))
            {
                File.Delete(_testFile);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}