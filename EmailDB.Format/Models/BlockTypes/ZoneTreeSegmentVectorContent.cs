using ProtoBuf;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class ZoneTreeSegmentVectorContent : BlockContent
{
    [ProtoMember(1)]
    public byte[] VectorData { get; set; }
    
    [ProtoMember(2)]
    public string SegmentId { get; set; }
    
    [ProtoMember(3)]
    public int Version { get; set; }
    
    [ProtoMember(4)]
    public string IndexType { get; set; } // Type of vector index (e.g., "HNSW", "IVF")
    
    public override BlockType GetBlockType() => BlockType.ZoneTreeSegment_Vector;
}