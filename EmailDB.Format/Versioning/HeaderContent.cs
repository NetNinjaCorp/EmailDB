using System;
using System.Collections.Generic;
using EmailDB.Format.Models;

namespace EmailDB.Format.Versioning;

/// <summary>
/// Represents the header content for database versioning.
/// </summary>
public class HeaderContent
{
    /// <summary>
    /// Encoded file version (Major.Minor.Patch).
    /// </summary>
    public int FileVersion { get; set; }
    
    /// <summary>
    /// Creation timestamp of the database.
    /// </summary>
    public long CreationTimestamp { get; set; }
    
    /// <summary>
    /// Last modification timestamp.
    /// </summary>
    public long LastModifiedTimestamp { get; set; }
    
    /// <summary>
    /// Offset to the first metadata block.
    /// </summary>
    public long FirstMetadataOffset { get; set; }
    
    /// <summary>
    /// Offset to the first folder tree block.
    /// </summary>
    public long FirstFolderTreeOffset { get; set; }
    
    /// <summary>
    /// Offset to the first cleanup/maintenance block.
    /// </summary>
    public long FirstCleanupOffset { get; set; }
    
    /// <summary>
    /// Feature capabilities flags for this database.
    /// </summary>
    public FeatureCapabilities Capabilities { get; set; }
    
    /// <summary>
    /// Block format versions for each block type.
    /// </summary>
    public Dictionary<Models.BlockType, int> BlockFormatVersions { get; set; } = new();
    
    /// <summary>
    /// Database-specific metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}