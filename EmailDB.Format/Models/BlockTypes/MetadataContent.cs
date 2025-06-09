using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class MetadataContent : BlockContent
{
    private const int metadataSpace = 10;
    
    [ProtoMember(1)]
    public long WALOffset { get; set; } = -1;
    
    [ProtoMember(2)]
    public long FolderTreeOffset { get; set; } = -1;
    
    [ProtoMember(3)]
    public Dictionary<string, long> SegmentOffsets { get; set; } = new();
    
    [ProtoMember(4)]
    public List<long> OutdatedOffsets { get; set; } = new();
    
    public override BlockType GetBlockType() => BlockType.Metadata;
}
