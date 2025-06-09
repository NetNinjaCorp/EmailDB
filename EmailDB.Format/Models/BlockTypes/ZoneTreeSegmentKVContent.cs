using ProtoBuf;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class ZoneTreeSegmentKVContent : BlockContent
{
    [ProtoMember(1)]
    public byte[] KeyValueData { get; set; }
    
    [ProtoMember(2)]
    public string SegmentId { get; set; }
    
    [ProtoMember(3)]
    public int Version { get; set; }
    
    public override BlockType GetBlockType() => BlockType.ZoneTreeSegment_KV;
}