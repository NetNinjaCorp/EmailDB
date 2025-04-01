using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;
[ProtoContract]
public class BlockHeader
{
    [ProtoMember(20)]
    public BlockType Type { get; set; }    

    [ProtoMember(21)]
    public long Timestamp { get; set; }

    [ProtoMember(22)]
    public uint Version { get; set; } = 1;

    [ProtoMember(23)]
    public uint Checksum { get; set; }
}