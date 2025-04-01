using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;
[ProtoContract]
public class FolderContent : BlockContent
{
    [ProtoMember(2000)]
    public long FolderId { get; set; }

    [ProtoMember(2001)]
    public long ParentFolderId { get; set; }

    [ProtoMember(2002)]
    public string Name { get; set; }

    [ProtoMember(2003)]
    public List<long> EmailIds { get; set; } = new();
}