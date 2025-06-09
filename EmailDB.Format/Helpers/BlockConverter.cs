using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Helpers;
internal static class BlockContentDeserializer
{
    internal static object? GetContent(this Block block, iBlockContentSerializer serializer)
    {
        return block.Type switch
        {
            BlockType.Metadata => serializer.Deserialize<MetadataContent>(block.Payload),
            BlockType.WAL => serializer.Deserialize<WALContent>(block.Payload),
            BlockType.FolderTree => serializer.Deserialize<FolderTreeContent>(block.Payload),
            BlockType.Folder => serializer.Deserialize<FolderContent>(block.Payload),
            BlockType.Segment => serializer.Deserialize<SegmentContent>(block.Payload),
            _ => null,
        };
    }
}
