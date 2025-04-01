using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.Blocks;

[ProtoContract]
public class Block
{
    [ProtoMember(1)]
    public uint Magic { get; set; } = 0xDEADBEEF;
    [ProtoMember(1)]
    public BlockHeader Header { get; set; }

    [ProtoMember(2)]
    public BlockContent Content { get; set; }
}