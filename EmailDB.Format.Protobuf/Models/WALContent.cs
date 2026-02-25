using ProtoBuf;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;
[ProtoContract]
public class WALContent : BlockContent
{
    [ProtoMember(5000, IsRequired = true)]
    public Dictionary<string, List<WALEntry>> Entries { get; set; } = new();

    [ProtoMember(5001), DefaultValue(-1L)]
    public long NextWALOffset { get; set; } = -1;

    [ProtoMember(5002, IsRequired = true)]
    public Dictionary<string, long> CategoryOffsets { get; set; } = new();
}

[ProtoContract]
public class WALEntry
{
    [ProtoMember(1)]
    public byte[] SerializedKey { get; set; }

    [ProtoMember(2)]
    public byte[] SerializedValue { get; set; }

    [ProtoMember(3)]
    public long OpIndex { get; set; }

    [ProtoMember(4)]
    public string Category { get; set; }

    [ProtoMember(5)]
    public long SegmentId { get; set; }
}