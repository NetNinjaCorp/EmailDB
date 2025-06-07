using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.Blocks;
[ProtoContract]
public class WALContent : BlockContent
{
    [ProtoMember(5000)]
    public Dictionary<string, List<WALEntry>> Entries { get; set; } = new();

    [ProtoMember(5001)]
    public Dictionary<string, long> CategoryOffsets { get; set; } = new();

    [ProtoMember(5002)]
    public long NextWALOffset { get; set; }
}

[ProtoContract]
public class WALEntry
{
    [ProtoMember(1)]
    public string Key { get; set; }
    
    [ProtoMember(2)]
    public byte[] Value { get; set; }
    
    [ProtoMember(3)]
    public long OpIndex { get; set; }
}