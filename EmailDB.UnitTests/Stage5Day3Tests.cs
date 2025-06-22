using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format;
using EmailDB.Format.Versioning;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for Stage 5 Day 3: Compatibility matrix and version-aware search.
/// </summary>
[Trait("Category", "Stage5")]
public class Stage5Day3Tests : IDisposable
{
    private readonly string _testFile;
    
    public Stage5Day3Tests()
    {
        _testFile = Path.GetTempFileName();
    }
    
    [Fact]
    public void CompatibilityMatrix_GetFeatureSet_ReturnsCorrectFeatures()
    {
        var v1Features = CompatibilityMatrix.GetFeatureSet(new DatabaseVersion(1, 0, 0));
        var v2Features = CompatibilityMatrix.GetFeatureSet(new DatabaseVersion(2, 0, 0));
        
        // V1 should have basic features
        Assert.Contains(DatabaseOperation.BasicEMLImport, v1Features.SupportedOperations);
        Assert.Contains(DatabaseOperation.FullTextSearch, v1Features.SupportedOperations);
        Assert.False(v1Features.CompressionSupported);
        Assert.False(v1Features.EncryptionSupported);
        
        // V2 should have advanced features
        Assert.Contains(DatabaseOperation.EmailBatching, v2Features.SupportedOperations);
        Assert.Contains(DatabaseOperation.BlockSuperseding, v2Features.SupportedOperations);
        Assert.True(v2Features.CompressionSupported);
        Assert.True(v2Features.EncryptionSupported);
    }
    
    [Fact]
    public void CompatibilityMatrix_GetCompatibilityRule_HandlesVersionComparisons()
    {
        var v1_0 = new DatabaseVersion(1, 0, 0);
        var v1_1 = new DatabaseVersion(1, 1, 0);
        var v2_0 = new DatabaseVersion(2, 0, 0);
        
        // Same version
        var sameRule = CompatibilityMatrix.GetCompatibilityRule(v1_0, v1_0);
        Assert.True(sameRule.IsCompatible);
        Assert.False(sameRule.RequiresMigration);
        Assert.True(sameRule.CanDirectlyOpen);
        
        // Minor upgrade
        var minorRule = CompatibilityMatrix.GetCompatibilityRule(v1_0, v1_1);
        Assert.True(minorRule.IsCompatible);
        Assert.False(minorRule.RequiresMigration);
        Assert.True(minorRule.CanDirectlyOpen);
        
        // Major upgrade
        var majorRule = CompatibilityMatrix.GetCompatibilityRule(v1_0, v2_0);
        Assert.True(majorRule.IsCompatible);
        Assert.True(majorRule.RequiresMigration);
        Assert.False(majorRule.CanDirectlyOpen);
        
        // Downgrade
        var downgradeRule = CompatibilityMatrix.GetCompatibilityRule(v2_0, v1_0);
        Assert.False(downgradeRule.IsCompatible);
    }
    
    [Fact]
    public void CompatibilityMatrix_IsOperationSupported_ChecksVersionCapabilities()
    {
        var v1 = new DatabaseVersion(1, 0, 0);
        var v2 = new DatabaseVersion(2, 0, 0);
        
        // Basic operations supported in both
        Assert.True(CompatibilityMatrix.IsOperationSupported(v1, DatabaseOperation.BasicEMLImport));
        Assert.True(CompatibilityMatrix.IsOperationSupported(v2, DatabaseOperation.BasicEMLImport));
        
        // Advanced operations only in v2
        Assert.False(CompatibilityMatrix.IsOperationSupported(v1, DatabaseOperation.EmailBatching));
        Assert.True(CompatibilityMatrix.IsOperationSupported(v2, DatabaseOperation.EmailBatching));
    }
    
    [Fact]
    public void CompatibilityMatrix_IsBlockTypeSupported_ChecksBlockSupport()
    {
        var v1 = new DatabaseVersion(1, 0, 0);
        var v2 = new DatabaseVersion(2, 0, 0);
        
        // Basic blocks supported in both
        Assert.True(CompatibilityMatrix.IsBlockTypeSupported(v1, BlockType.Metadata));
        Assert.True(CompatibilityMatrix.IsBlockTypeSupported(v2, BlockType.Metadata));
        
        // Advanced blocks only in v2
        Assert.False(CompatibilityMatrix.IsBlockTypeSupported(v1, BlockType.FolderEnvelope));
        Assert.True(CompatibilityMatrix.IsBlockTypeSupported(v2, BlockType.FolderEnvelope));
    }
    
    [Fact]
    public void CompatibilityMatrix_GetUpgradePath_ReturnsCorrectPath()
    {
        var v1_0 = new DatabaseVersion(1, 0, 0);
        var v2_0 = new DatabaseVersion(2, 0, 0);
        var v3_0 = new DatabaseVersion(3, 0, 0);
        
        // Single major version upgrade
        var path1 = CompatibilityMatrix.GetUpgradePath(v1_0, v2_0);
        Assert.Single(path1);
        Assert.Equal(v2_0, path1[0]);
        
        // Multiple major version upgrade
        var path2 = CompatibilityMatrix.GetUpgradePath(v1_0, v3_0);
        Assert.Equal(2, path2.Count);
        Assert.Equal(v2_0, path2[0]);
        Assert.Equal(v3_0, path2[1]);
        
        // No upgrade needed
        var path3 = CompatibilityMatrix.GetUpgradePath(v2_0, v1_0);
        Assert.Empty(path3);
    }
    
    [Fact]
    public async Task EmailDatabase_GetAvailableSearchFeatures_ReturnsVersionSpecificFeatures()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var features = emailDB.GetAvailableSearchFeatures();
        
        Assert.NotNull(features);
        Assert.True(features.SupportsBasicSearch);
        
        // Version 2.0.0 should support advanced features
        Assert.True(features.SupportsAdvancedSearch);
        Assert.True(features.SupportsRegexSearch);
        Assert.True(features.SupportsFieldSearch);
        Assert.True(features.SupportsDateRangeFiltering);
    }
    
    [Fact]
    public async Task EmailDatabase_SearchWithVersionAwareness_HandlesBasicSearch()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Import a test email first
        var testEml = @"From: test@example.com
To: recipient@example.com
Subject: Version Awareness Test
Date: Mon, 1 Jan 2024 12:00:00 +0000

This email tests version-aware search functionality.";

        await emailDB.ImportEMLAsync(testEml, "test.eml");
        
        var searchResult = await emailDB.SearchWithVersionAwarenessAsync("version");
        
        Assert.True(searchResult.IsSuccess);
        Assert.NotEmpty(searchResult.Value);
        
        var result = searchResult.Value.First();
        Assert.Equal("Basic", result.SearchMethod);
        Assert.NotNull(result.DatabaseVersion);
        Assert.NotNull(result.AvailableFeatures);
    }
    
    [Fact]
    public async Task EmailDatabase_SmartSearch_AutoDetectsQueryType()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Import test emails
        var testEml1 = @"From: alice@example.com
Subject: Meeting Notes
Date: Mon, 1 Jan 2024 12:00:00 +0000

Important meeting notes about project.";

        var testEml2 = @"From: bob@example.com
Subject: Project Update
Date: Tue, 2 Jan 2024 12:00:00 +0000

Project update with latest information.";

        await emailDB.ImportEMLAsync(testEml1, "test1.eml");
        await emailDB.ImportEMLAsync(testEml2, "test2.eml");
        
        // Basic query
        var basicResult = await emailDB.SmartSearchAsync("project");
        Assert.True(basicResult.IsSuccess);
        Assert.NotEmpty(basicResult.Value);
        
        // Advanced query (should be detected automatically)
        var advancedResult = await emailDB.SmartSearchAsync("from:alice AND subject:meeting");
        Assert.True(advancedResult.IsSuccess);
    }
    
    [Fact]
    public async Task EmailDatabase_SearchWithVersionAwareness_RespectsVersionLimits()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Test with high max results that should be limited by version
        var options = new SearchOptions
        {
            MaxResults = 100000 // Very high number
        };
        
        var result = await emailDB.SearchWithVersionAwarenessAsync("test", options);
        
        Assert.True(result.IsSuccess);
        
        // Should be limited by version capabilities
        var featureSet = CompatibilityMatrix.GetFeatureSet(emailDB.DatabaseVersion);
        Assert.True(result.Value.Count <= featureSet.MaximumEmailsPerBatch);
    }
    
    [Fact]
    public async Task SearchOptions_DateFiltering_WorksCorrectly()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var options = new SearchOptions
        {
            DateFrom = new DateTime(2024, 1, 1),
            DateTo = new DateTime(2024, 12, 31),
            MaxResults = 10
        };
        
        var result = await emailDB.SearchWithVersionAwarenessAsync("test", options);
        
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
    }
    
    [Fact]
    public void SearchFeatureSet_HasCorrectDefaults()
    {
        var features = new SearchFeatureSet();
        
        Assert.False(features.SupportsBasicSearch);
        Assert.False(features.SupportsAdvancedSearch);
        Assert.Equal(0, features.MaxSearchTerms);
        Assert.Equal(0, features.MaxResultsPerPage);
    }
    
    [Fact]
    public void VersionFeatureSet_HasCorrectStructure()
    {
        var featureSet = new VersionFeatureSet
        {
            Version = new DatabaseVersion(1, 0, 0),
            SupportedOperations = new[] { DatabaseOperation.BasicEMLImport },
            MaximumEmailsPerBatch = 1000,
            CompressionSupported = false
        };
        
        Assert.Equal(1, featureSet.Version.Major);
        Assert.Single(featureSet.SupportedOperations);
        Assert.Equal(1000, featureSet.MaximumEmailsPerBatch);
        Assert.False(featureSet.CompressionSupported);
    }
    
    [Fact]
    public void CompatibilityRule_HasCorrectDefaults()
    {
        var rule = new CompatibilityRule
        {
            IsCompatible = true,
            RequiresMigration = false,
            Notes = "Test rule"
        };
        
        Assert.True(rule.IsCompatible);
        Assert.False(rule.RequiresMigration);
        Assert.Equal("Test rule", rule.Notes);
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
/// Integration tests for complete Stage 5 workflow.
/// </summary>
[Trait("Category", "Stage5")]
public class Stage5IntegrationTests : IDisposable
{
    private readonly string _testFile;
    
    public Stage5IntegrationTests()
    {
        _testFile = Path.GetTempFileName();
    }
    
    [Fact]
    public async Task Complete_Stage5_Workflow_Works()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // 1. Check database version
        var version = emailDB.DatabaseVersion;
        Assert.NotNull(version);
        
        // 2. Get compatibility info
        var compatibility = await emailDB.GetVersionCompatibilityAsync();
        Assert.True(compatibility.IsCompatible);
        
        // 3. Check supported operations
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.BasicEMLImport));
        Assert.True(emailDB.IsOperationSupported(DatabaseOperation.FullTextSearch));
        
        // 4. Import email with version checking
        var testEml = @"From: integration@test.com
Subject: Stage 5 Integration Test
Date: Mon, 1 Jan 2024 12:00:00 +0000

This email tests the complete Stage 5 integration workflow.";

        var importResult = await emailDB.ImportEMLWithVersionCheckAsync(testEml, "integration.eml");
        Assert.True(importResult.IsSuccess);
        
        // 5. Perform version-aware search
        var searchResult = await emailDB.SearchWithVersionAwarenessAsync("integration");
        Assert.True(searchResult.IsSuccess);
        Assert.NotEmpty(searchResult.Value);
        
        // 6. Use smart search
        var smartResult = await emailDB.SmartSearchAsync("subject:integration");
        Assert.True(smartResult.IsSuccess);
        
        // 7. Get detailed version info
        var versionInfo = await emailDB.GetDetailedVersionInfoAsync();
        Assert.NotNull(versionInfo);
        Assert.NotEmpty(versionInfo.SupportedOperations);
        
        // 8. Check migration planning
        var migrationPlan = await emailDB.PlanMigrationAsync(DatabaseVersion.Current);
        Assert.True(migrationPlan.IsSuccess);
        Assert.True(migrationPlan.Value.IsPossible);
    }
    
    [Fact]
    public async Task Stage5_BatchImport_WithProgressReporting_Works()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var emails = new[]
        {
            ("email1.eml", @"From: user1@test.com
Subject: Test Email 1
Date: Mon, 1 Jan 2024 12:00:00 +0000

First test email."),
            ("email2.eml", @"From: user2@test.com
Subject: Test Email 2
Date: Tue, 2 Jan 2024 12:00:00 +0000

Second test email."),
            ("email3.eml", @"From: user3@test.com
Subject: Test Email 3
Date: Wed, 3 Jan 2024 12:00:00 +0000

Third test email.")
        };
        
        var progressReports = new List<BatchImportProgress>();
        var progress = new Progress<BatchImportProgress>(p => progressReports.Add(p));
        
        var result = await emailDB.ImportEMLBatchWithVersionCheckAsync(emails, progress);
        
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.SuccessCount);
        Assert.Equal(0, result.Value.ErrorCount);
        Assert.NotEmpty(progressReports);
        Assert.Equal(100, progressReports.Last().ProgressPercentage);
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