using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;
[ProtoContract]
public class BlockHeader
{
    [ProtoMember(20)]
    public BlockType Type { get; set; }

    [ProtoMember(21)]
    public long NextBlockOffset { get; set; } = -1;

    [ProtoMember(22)]
    public long Timestamp { get; set; }

    [ProtoMember(23)]
    public uint Version { get; set; } = 1;

    [ProtoMember(24)]
    public uint Checksum { get; set; }
}