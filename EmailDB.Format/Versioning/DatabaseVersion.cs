using System;
using System.Collections.Generic;
using EmailDB.Format.Models;

namespace EmailDB.Format.Versioning;

/// <summary>
/// Feature capabilities that can be enabled in a database version.
/// These are externally documented, not stored in the file.
/// </summary>
[Flags]
public enum FeatureCapabilities : long
{
    None = 0,
    Compression = 1 << 0,           // Block-level compression
    Encryption = 1 << 1,            // Block-level encryption
    EmailBatching = 1 << 2,         // Multiple emails per block
    EnvelopeBlocks = 1 << 3,        // Fast folder listings
    InBandKeyManagement = 1 << 4,   // Encryption key storage
    HashChainIntegrity = 1 << 5,    // Block integrity validation
    FullTextSearch = 1 << 6,        // Search indexing
    FolderHierarchy = 1 << 7,       // Nested folder support
    EmailDeduplication = 1 << 8,    // Duplicate detection
    BlockSuperseding = 1 << 9,      // Stage 4 maintenance
    AtomicTransactions = 1 << 10,   // ACID operations
    
    // Version-specific capability sets (externally documented)
    V1Capabilities = Compression | Encryption | FolderHierarchy,
    V2Capabilities = V1Capabilities | EmailBatching | EnvelopeBlocks | 
                     InBandKeyManagement | HashChainIntegrity | 
                     FullTextSearch | BlockSuperseding
}

public enum UpgradeType
{
    NotSupported,
    InPlace,      // Can upgrade without migration (minor/patch)
    Migration     // Requires data migration (major)
}

/// <summary>
/// Represents the database format version using semantic versioning.
/// </summary>
public class DatabaseVersion : IComparable<DatabaseVersion>, IEquatable<DatabaseVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public FeatureCapabilities Capabilities { get; }
    public Dictionary<BlockType, int> BlockFormatVersions { get; }
    
    public DatabaseVersion(int major, int minor, int patch, 
        FeatureCapabilities capabilities = FeatureCapabilities.None)
    {
        // Validation
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor));
        if (patch < 0) throw new ArgumentOutOfRangeException(nameof(patch));
        
        Major = major;
        Minor = minor;
        Patch = patch;
        Capabilities = capabilities;
        BlockFormatVersions = new Dictionary<BlockType, int>();
        
        InitializeBlockVersions();
    }
    
    /// <summary>
    /// Current version of the EmailDB format.
    /// </summary>
    public static DatabaseVersion Current => new DatabaseVersion(2, 0, 0, 
        FeatureCapabilities.V2Capabilities);
    
    /// <summary>
    /// Minimum supported version for this implementation.
    /// </summary>
    public static DatabaseVersion MinimumSupported => new DatabaseVersion(1, 0, 0,
        FeatureCapabilities.V1Capabilities);
    
    private void InitializeBlockVersions()
    {
        if (Major == 1)
        {
            // V1 block format versions
            BlockFormatVersions[BlockType.Metadata] = 1;
            BlockFormatVersions[BlockType.WAL] = 1;
            BlockFormatVersions[BlockType.Folder] = 1;
            BlockFormatVersions[BlockType.EmailBatch] = 0;  // Not supported
            BlockFormatVersions[BlockType.FolderEnvelope] = 0;  // Not supported
        }
        else if (Major == 2)
        {
            // V2 block format versions
            BlockFormatVersions[BlockType.Metadata] = 1;
            BlockFormatVersions[BlockType.WAL] = 1;
            BlockFormatVersions[BlockType.Folder] = 2;  // Enhanced for envelopes
            BlockFormatVersions[BlockType.EmailBatch] = 1;  // New in v2
            BlockFormatVersions[BlockType.FolderEnvelope] = 1;  // New in v2
            BlockFormatVersions[BlockType.KeyManager] = 1;  // New in v2
            BlockFormatVersions[BlockType.KeyExchange] = 1;  // New in v2
        }
    }
    
    #region Comparison Methods
    
    public int CompareTo(DatabaseVersion other)
    {
        if (other == null) return 1;
        
        int majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0) return majorCompare;
        
        int minorCompare = Minor.CompareTo(other.Minor);
        if (minorCompare != 0) return minorCompare;
        
        return Patch.CompareTo(other.Patch);
    }
    
    public bool Equals(DatabaseVersion other)
    {
        if (other == null) return false;
        return Major == other.Major && Minor == other.Minor && Patch == other.Patch;
    }
    
    public override bool Equals(object obj)
    {
        return Equals(obj as DatabaseVersion);
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(Major, Minor, Patch);
    }
    
    #endregion
    
    #region Compatibility Methods
    
    public bool IsCompatibleWith(DatabaseVersion other)
    {
        if (other == null) return false;
        
        // Same major version = compatible
        if (Major == other.Major) return true;
        
        // Can read older major versions if >= minimum supported
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
    
    #endregion
    
    #region Operators
    
    public static bool operator ==(DatabaseVersion left, DatabaseVersion right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }
    
    public static bool operator !=(DatabaseVersion left, DatabaseVersion right)
    {
        return !(left == right);
    }
    
    public static bool operator >=(DatabaseVersion left, DatabaseVersion right)
    {
        if (left is null) return right is null;
        return left.CompareTo(right) >= 0;
    }
        
    public static bool operator >(DatabaseVersion left, DatabaseVersion right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }
        
    public static bool operator <=(DatabaseVersion left, DatabaseVersion right)
    {
        if (left is null) return true;
        return left.CompareTo(right) <= 0;
    }
        
    public static bool operator <(DatabaseVersion left, DatabaseVersion right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }
    
    #endregion
    
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}