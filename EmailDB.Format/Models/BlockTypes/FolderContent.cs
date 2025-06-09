using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EmailDB.Format.Models.EmailContent;
using ProtoBuf;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class FolderContent : BlockContent
{
    [ProtoMember(1)]
    public long FolderId { get; set; }

    [ProtoMember(2)]
    public long ParentFolderId { get; set; }

    [ProtoMember(3)]
    public string Name { get; set; }

    [ProtoMember(4)]
    public List<EmailHashedID> EmailIds { get; set; } = new();
    
    [ProtoMember(10)]
    public long EnvelopeBlockId { get; set; }
    
    [ProtoMember(11)]
    public int Version { get; set; }
    
    [ProtoMember(12)]
    public DateTime LastModified { get; set; }
    
    public override BlockType GetBlockType() => BlockType.Folder;
}