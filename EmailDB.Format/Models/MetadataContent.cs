using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;
[ProtoContract]
public class MetadataContent : BlockContent
{
    [ProtoMember(3500)]
    public long WALOffset { get; set; } = -1;
    [ProtoMember(3501)]
    public long FolderTreeOffset { get; set; } = -1;

    [ProtoMember(3502)]
    public Dictionary<string, long> SegmentOffsets { get; set; } = new();

    [ProtoMember(3503)]
    public List<long> OutdatedOffsets { get; set; } = new();

    

  
}
