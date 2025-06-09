using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class HeaderContent : BlockContent
{
    [ProtoMember(1)]
    public int FileVersion { get; set; }
    
    [ProtoMember(2)]
    public long FirstMetadataOffset { get; set; }
    
    [ProtoMember(3)]
    public long FirstFolderTreeOffset { get; set; }
    
    [ProtoMember(4)]
    public long FirstCleanupOffset { get; set; }
    
    public override BlockType GetBlockType() => BlockType.Metadata; // Headers are stored as metadata
}
