using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
[ProtoInclude(1, typeof(FolderContent))]
[ProtoInclude(2, typeof(MetadataContent))]
[ProtoInclude(3, typeof(HeaderContent))]
[ProtoInclude(4, typeof(FolderEnvelopeBlock))]
[ProtoInclude(5, typeof(ZoneTreeSegmentKVContent))]
[ProtoInclude(6, typeof(ZoneTreeSegmentVectorContent))]
public abstract class BlockContent 
{
    public abstract BlockType GetBlockType();
}
