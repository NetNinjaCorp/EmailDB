using System;
using System.Text;
using System.Text.Json;
using EmailDB.Format;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Helpers;

namespace EmailDB.UnitTests.Helpers;

public static class BlockTestHelpers
{
    private static readonly DefaultBlockContentSerializer _serializer = new DefaultBlockContentSerializer();
    
    public static Block CreateSegmentBlock(long blockId, string data, PayloadEncoding encoding = PayloadEncoding.Json)
    {
        var content = new SegmentContent
        {
            SegmentId = blockId,
            SegmentData = Encoding.UTF8.GetBytes(data),
            FileName = $"segment_{blockId}.dat",
            FileOffset = 0,
            ContentLength = data.Length,
            SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsDeleted = false,
            Version = 1
        };
        
        return new Block
        {
            BlockId = blockId,
            Type = BlockType.Segment,
            Encoding = encoding,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = _serializer.Serialize(content),
            Version = 1,
            Flags = 0
        };
    }
    
    public static Block CreateMetadataBlock(long blockId, MetadataContent metadata, PayloadEncoding encoding = PayloadEncoding.Json)
    {
        return new Block
        {
            BlockId = blockId,
            Type = BlockType.Metadata,
            Encoding = encoding,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = _serializer.Serialize(metadata),
            Version = 1,
            Flags = 0
        };
    }
    
    public static Block CreateWALBlock(long blockId, WALContent wal, PayloadEncoding encoding = PayloadEncoding.Json)
    {
        return new Block
        {
            BlockId = blockId,
            Type = BlockType.WAL,
            Encoding = encoding,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = _serializer.Serialize(wal),
            Version = 1,
            Flags = 0
        };
    }
    
    public static Block CreateFolderBlock(long blockId, FolderContent folder, PayloadEncoding encoding = PayloadEncoding.Json)
    {
        return new Block
        {
            BlockId = blockId,
            Type = BlockType.Folder,
            Encoding = encoding,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = _serializer.Serialize(folder),
            Version = 1,
            Flags = 0
        };
    }
    
    public static T GetContent<T>(Block block) where T : class
    {
        return _serializer.Deserialize<T>(block.Payload);
    }
    
    public static SegmentContent GetSegmentContent(Block block)
    {
        if (block.Type != BlockType.Segment)
            throw new InvalidOperationException($"Block is not a segment block, it's a {block.Type}");
        return GetContent<SegmentContent>(block);
    }
}