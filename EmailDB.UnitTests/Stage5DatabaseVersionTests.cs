using System;
using System.Linq;
using Xunit;
using EmailDB.Format.Versioning;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for Stage 5 DatabaseVersion functionality.
/// </summary>
[Trait("Category", "Stage5")]
public class Stage5DatabaseVersionTests
{
    [Fact]
    public void DatabaseVersion_Construction_Works()
    {
        var version = new DatabaseVersion(2, 1, 3);
        
        Assert.Equal(2, version.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(3, version.Patch);
        Assert.Equal("2.1.3", version.ToString());
    }
    
    [Fact]
    public void DatabaseVersion_Validation_RejectsNegativeNumbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseVersion(1, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseVersion(1, 0, -1));
    }
    
    [Fact]
    public void DatabaseVersion_Comparison_Works()
    {
        var v1_0_0 = new DatabaseVersion(1, 0, 0);
        var v1_1_0 = new DatabaseVersion(1, 1, 0);
        var v1_1_1 = new DatabaseVersion(1, 1, 1);
        var v2_0_0 = new DatabaseVersion(2, 0, 0);
        
        // Less than comparisons
        Assert.True(v1_0_0 < v1_1_0);
        Assert.True(v1_1_0 < v1_1_1);
        Assert.True(v1_1_1 < v2_0_0);
        
        // Greater than comparisons
        Assert.True(v2_0_0 > v1_1_1);
        Assert.True(v1_1_1 > v1_1_0);
        Assert.True(v1_1_0 > v1_0_0);
        
        // Equality
        var v1_0_0_copy = new DatabaseVersion(1, 0, 0);
        Assert.True(v1_0_0 == v1_0_0_copy);
        Assert.True(v1_0_0.Equals(v1_0_0_copy));
        Assert.False(v1_0_0 != v1_0_0_copy);
    }
    
    [Fact]
    public void DatabaseVersion_Compatibility_Works()
    {
        var v1_0_0 = new DatabaseVersion(1, 0, 0);
        var v1_1_0 = new DatabaseVersion(1, 1, 0);
        var v2_0_0 = new DatabaseVersion(2, 0, 0);
        var v3_0_0 = new DatabaseVersion(3, 0, 0);
        
        // Same major version = compatible
        Assert.True(v1_1_0.IsCompatibleWith(v1_0_0));
        Assert.True(v1_0_0.IsCompatibleWith(v1_1_0));
        
        // Newer can read older if >= minimum supported
        Assert.True(v2_0_0.IsCompatibleWith(v1_0_0));
        
        // Older cannot read newer
        Assert.False(v1_0_0.IsCompatibleWith(v2_0_0));
    }
    
    [Fact]
    public void DatabaseVersion_UpgradeType_Works()
    {
        var v1_0_0 = new DatabaseVersion(1, 0, 0);
        var v1_0_1 = new DatabaseVersion(1, 0, 1);
        var v1_1_0 = new DatabaseVersion(1, 1, 0);
        var v2_0_0 = new DatabaseVersion(2, 0, 0);
        var v3_0_0 = new DatabaseVersion(3, 0, 0);
        
        // Patch upgrades = in-place
        Assert.Equal(UpgradeType.InPlace, v1_0_0.GetUpgradeType(v1_0_1));
        
        // Minor upgrades = in-place
        Assert.Equal(UpgradeType.InPlace, v1_0_0.GetUpgradeType(v1_1_0));
        
        // Major upgrades = migration
        Assert.Equal(UpgradeType.Migration, v1_0_0.GetUpgradeType(v2_0_0));
        
        // Skip major versions = not supported
        Assert.Equal(UpgradeType.NotSupported, v1_0_0.GetUpgradeType(v3_0_0));
        
        // Downgrades = not supported
        Assert.Equal(UpgradeType.NotSupported, v2_0_0.GetUpgradeType(v1_0_0));
    }
    
    [Fact]
    public void DatabaseVersion_CanUpgradeTo_Works()
    {
        var v1_0_0 = new DatabaseVersion(1, 0, 0);
        var v1_1_0 = new DatabaseVersion(1, 1, 0);
        var v2_0_0 = new DatabaseVersion(2, 0, 0);
        var v3_0_0 = new DatabaseVersion(3, 0, 0);
        
        // Can upgrade within major version
        Assert.True(v1_0_0.CanUpgradeTo(v1_1_0));
        
        // Can upgrade to next major version
        Assert.True(v1_0_0.CanUpgradeTo(v2_0_0));
        
        // Cannot skip major versions
        Assert.False(v1_0_0.CanUpgradeTo(v3_0_0));
        
        // Cannot downgrade
        Assert.False(v2_0_0.CanUpgradeTo(v1_0_0));
        
        // Cannot upgrade to same version
        Assert.False(v1_0_0.CanUpgradeTo(v1_0_0));
    }
    
    [Fact]
    public void DatabaseVersion_Current_IsValid()
    {
        var current = DatabaseVersion.Current;
        
        Assert.NotNull(current);
        Assert.Equal(2, current.Major);
        Assert.Equal(0, current.Minor);
        Assert.Equal(0, current.Patch);
        Assert.True(current.Capabilities.HasFlag(FeatureCapabilities.EmailBatching));
        Assert.True(current.Capabilities.HasFlag(FeatureCapabilities.BlockSuperseding));
    }
    
    [Fact]
    public void DatabaseVersion_MinimumSupported_IsValid()
    {
        var minimum = DatabaseVersion.MinimumSupported;
        
        Assert.NotNull(minimum);
        Assert.Equal(1, minimum.Major);
        Assert.Equal(0, minimum.Minor);
        Assert.Equal(0, minimum.Patch);
        Assert.True(minimum.Capabilities.HasFlag(FeatureCapabilities.FolderHierarchy));
    }
    
    [Fact]
    public void DatabaseVersion_BlockFormatVersions_AreInitialized()
    {
        var v1 = new DatabaseVersion(1, 0, 0);
        var v2 = new DatabaseVersion(2, 0, 0);
        
        // V1 should not support new block types
        Assert.Equal(0, v1.BlockFormatVersions[BlockType.EmailBatch]);
        Assert.Equal(0, v1.BlockFormatVersions[BlockType.FolderEnvelope]);
        
        // V2 should support new block types
        Assert.Equal(1, v2.BlockFormatVersions[BlockType.EmailBatch]);
        Assert.Equal(1, v2.BlockFormatVersions[BlockType.FolderEnvelope]);
        Assert.Equal(2, v2.BlockFormatVersions[BlockType.Folder]); // Enhanced
    }
    
    [Fact]
    public void FeatureCapabilities_Flags_Work()
    {
        var v2Caps = FeatureCapabilities.V2Capabilities;
        
        Assert.True(v2Caps.HasFlag(FeatureCapabilities.Compression));
        Assert.True(v2Caps.HasFlag(FeatureCapabilities.EmailBatching));
        Assert.True(v2Caps.HasFlag(FeatureCapabilities.BlockSuperseding));
        Assert.True(v2Caps.HasFlag(FeatureCapabilities.FullTextSearch));
        
        // Should also include V1 capabilities
        Assert.True(v2Caps.HasFlag(FeatureCapabilities.V1Capabilities));
    }
}