using System;
using System.Collections.Generic;

namespace EmailDB.UnitTests.Models;

// Mock models for testing
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

public enum BlockType
{
    Header = 0,
    Metadata = 1,
    Folder = 2,
    Email = 3,
    Segment = 4,
    FolderTree = 5
}

public class BlockContent
{
    public MetadataContent MetadataContent { get; set; }
}

public class Block
{
    public ushort Version { get; set; }
    public BlockType Type { get; set; }
    public byte Flags { get; set; }
    public long Timestamp { get; set; }
    public long BlockId { get; set; }
    public byte[] Payload { get; set; }
    public BlockContent Content { get; set; }
}

public class BlockLocation
{
    public long Position { get; set; }
    public long Length { get; set; }
}