using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;
[ProtoContract]
public class CleanupContent : BlockContent
{
    [ProtoMember(1000)]
    public List<long> FolderTreeOffsets { get; set; } = new();

    [ProtoMember(1001)]
    public Dictionary<string, List<long>> FolderOffsets { get; set; } = new();

    [ProtoMember(1002)]
    public List<long> MetadataOffsets { get; set; } = new();

    [ProtoMember(1003)]
    public long CleanupThreshold { get; set; }
}