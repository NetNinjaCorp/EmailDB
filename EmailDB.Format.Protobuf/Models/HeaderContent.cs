using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;
[ProtoContract]
public class HeaderContent : BlockContent
{
    [ProtoMember(3000)]
    public int FileVersion { get; set; }

    [ProtoMember(3001)]
    public long FirstMetadataOffset { get; set; }

    [ProtoMember(3002)]
    public long FirstFolderTreeOffset { get; set; }

    [ProtoMember(3003)]
    public long FirstCleanupOffset { get; set; }
}
