using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.Blocks;
[ProtoContract]
public class FolderTreeContent : BlockContent
{
    [ProtoMember(2500)]
    public long RootFolderId { get; set; }

    [ProtoMember(2501)]
    public Dictionary<string, string> FolderHierarchy { get; set; } = new();

    [ProtoMember(2502)]
    public Dictionary<string, long> FolderIDs { get; set; } = new();

    [ProtoMember(2503)]
    public Dictionary<long, long> FolderOffsets { get; set; } = new();

}
