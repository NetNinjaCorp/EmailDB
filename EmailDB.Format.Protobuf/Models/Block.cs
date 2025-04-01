using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;

[ProtoContract]
public class Block
{
    [ProtoMember(1)]
    public uint Magic { get; set; } = 0xDEADBEEF;
    [ProtoMember(2)]
    public BlockHeader Header { get; set; }

    [ProtoMember(3)]
    public BlockContent Content { get; set; }
}