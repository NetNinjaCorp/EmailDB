using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.Blocks;
[ProtoContract]
public class MetadataContent : BlockContent
{
    private const int metadataSpace = 10;

    [ProtoMember(3500)]
    public long WALOffset { get; set; } = -1;
    [ProtoMember(3501)]
    public long FolderTreeOffset { get; set; } = -1;

    [ProtoMember(3502)]
    public Dictionary<string, long> SegmentOffsets { get; set; } = new();

    [ProtoMember(3503)]
    public List<long> OutdatedOffsets { get; set; } = new();



    public byte[] SerializeMetadata()
    {
        using (var ms = new MemoryStream())
        {
            Serializer.SerializeWithLengthPrefix(ms, this, PrefixStyle.Base128);
            return ms.ToArray(); // Return final 10MB metadata block
        }
    }

}
