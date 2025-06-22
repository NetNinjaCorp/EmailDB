using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format;
using EmailDB.Format.Versioning;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for Stage 5 Versioning functionality with EmailDatabase.
/// </summary>
[Trait("Category", "Stage5")]
public class Stage5HighLevelAPITests : IDisposable
{
    private readonly string _testFile;
    
    public Stage5HighLevelAPITests()
    {
        _testFile = Path.GetTempFileName();
    }
    
    [Fact]
    public async Task EmailDatabase_DatabaseVersion_IsNotNull()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var version = emailDB.DatabaseVersion;
        
        Assert.NotNull(version);
        Assert.True(version.Major >= 0);
        Assert.True(version.Minor >= 0);
        Assert.True(version.Patch >= 0);
    }
    
    [Fact]
    public async Task EmailDatabase_GetVersionCompatibilityAsync_ReturnsValidInfo()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var compatibility = await emailDB.GetVersionCompatibilityAsync();
        
        Assert.NotNull(compatibility);
        Assert.NotNull(compatibility.DatabaseVersion);
        Assert.NotNull(compatibility.ImplementationVersion);
        Assert.NotNull(compatibility.Message);
    }
    
    [Fact]
    public async Task EmailDatabase_NewDatabase_HasCurrentVersion()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var version = emailDB.DatabaseVersion;
        var current = DatabaseVersion.Current;
        
        // New database should default to current version
        Assert.Equal(current.Major, version.Major);
        Assert.Equal(current.Minor, version.Minor);
        Assert.Equal(current.Patch, version.Patch);
    }
    
    [Fact]
    public async Task EmailDatabase_VersionCompatibility_IsCompatibleWithItself()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var compatibility = await emailDB.GetVersionCompatibilityAsync();
        
        // Database should be compatible with itself
        Assert.True(compatibility.IsCompatible);
        Assert.Contains("compatible", compatibility.Message.ToLowerInvariant());
    }
    
    [Fact]
    public async Task EmailDatabase_ExistingDatabase_UsesCorrectStats()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        // Use existing GetDatabaseStatsAsync method
        var stats = await emailDB.GetDatabaseStatsAsync();
        
        Assert.NotNull(stats);
        Assert.True(stats.TotalEmails >= 0);
        Assert.True(stats.StorageBlocks >= 0);
        Assert.True(stats.SearchIndexes >= 0);
        Assert.True(stats.TotalFolders >= 0);
    }
    
    [Fact]
    public async Task EmailDatabase_EmptyDatabase_HasZeroStats()
    {
        using var emailDB = new EmailDatabase(_testFile);
        
        var stats = await emailDB.GetDatabaseStatsAsync();
        
        // Empty database should have zero counts
        Assert.Equal(0, stats.TotalEmails);
        Assert.Equal(0, stats.TotalFolders);
        Assert.Equal(0, stats.SearchIndexes);
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