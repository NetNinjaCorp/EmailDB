using ProtoBuf;

namespace EmailDB.Format.Protobuf.Models;

[ProtoContract]
public class MetadataPayload
{
    [ProtoMember(1)]
    public uint FileFormatVersion { get; set; }

    [ProtoMember(2)]
    public long RootFolderTreeId { get; set; }

    [ProtoMember(3)]
    public long NextBlockId { get; set; }

    [ProtoMember(4)]
    public long CreationTimestampTicks { get; set; }

    [ProtoMember(5)]
    public long LastCompactionTimestampTicks { get; set; }
}
