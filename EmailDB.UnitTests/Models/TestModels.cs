using System;
using System.Collections.Generic;

namespace EmailDB.UnitTests.Models;

// Mock models for testing

// Simple test version of EmailHashedID
public struct TestEmailHashedID
{
    public string Id { get; set; }
    
    public TestEmailHashedID(string id)
    {
        Id = id;
    }
    
    public static implicit operator TestEmailHashedID(string id)
    {
        return new TestEmailHashedID(id);
    }
    
    public static implicit operator string(TestEmailHashedID hashId)
    {
        return hashId.Id;
    }
}

public class FolderContent
{
    public string Name { get; set; }
    public List<string> EmailIds { get; set; } = new List<string>();
}

public class SegmentContent
{
    public long SegmentId { get; set; }
    public byte[] SegmentData { get; set; }
}

public class FolderTreeContent
{
    public List<FolderHierarchyItem> FolderHierarchy { get; set; } = new List<FolderHierarchyItem>();
}

public class FolderHierarchyItem
{
    public string Name { get; set; }
    public string ParentName { get; set; }
}

// Test-specific metadata content that extends the actual one
public class TestMetadataContent
{
    // Properties from actual MetadataContent
    public long WALOffset { get; set; } = -1;
    public long FolderTreeOffset { get; set; } = -1;
    public Dictionary<string, long> SegmentOffsets { get; set; } = new();
    public List<long> OutdatedOffsets { get; set; } = new();
    
    // Additional properties for testing
    public string Version { get; set; }
    public DateTime CreationDate { get; set; }
    public long CreatedTimestamp { get; set; }
    public long LastModifiedTimestamp { get; set; }
    public long BlockCount { get; set; }
    public long FileSize { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
}

// Keep original for compatibility
public class MetadataContent
{
    public string Version { get; set; }
    public DateTime CreationDate { get; set; }
}

public class HeaderContent
{
    public int FileVersion { get; set; }
    public long FirstMetadataOffset { get; set; } = -1;
    public long FirstFolderTreeOffset { get; set; } = -1;
}

// Removed BlockType enum - use EmailDB.Format.Models.BlockType instead

public class BlockContent
{
    public FolderContent FolderContent { get; set; }
    public SegmentContent SegmentContent { get; set; }
    public FolderTreeContent FolderTreeContent { get; set; }
    public MetadataContent MetadataContent { get; set; }
}

// Removed test Block and BlockLocation - use EmailDB.Format.Models types instead

// Test cleanup content
public class CleanupContent
{
    public int Version { get; set; }
    public long CleanupTimestamp { get; set; }
    public List<long> BlocksToRemove { get; set; } = new();
    public List<long> BlocksToClean { get; set; } = new();
    public DateTime ScheduledTime { get; set; }
    public string Reason { get; set; }
}