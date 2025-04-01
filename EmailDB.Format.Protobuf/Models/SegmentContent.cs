using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Protobuf.Models;
[ProtoContract]
public class SegmentContent : BlockContent
{
    [ProtoMember(4000)]
    public long SegmentId { get; set; }

    [ProtoMember(4001)]
    public byte[] SegmentData { get; set; }

    // New fields for ZoneTree integration
    [ProtoMember(4002)]
    public string FileName { get; set; }  // Name of the physical file containing this segment

    [ProtoMember(4003)]
    public long FileOffset { get; set; }  // Offset within the file where the segment data begins

    [ProtoMember(4004)]
    public int ContentLength { get; set; }  // Length of the email content in bytes

    [ProtoMember(4005)]
    public long SegmentTimestamp { get; set; }  // When this segment version was created

    [ProtoMember(4006)]
    public bool IsDeleted { get; set; }  // Soft deletion flag

    [ProtoMember(4007)]
    public uint Version { get; set; }  // Version number for this segment

    [ProtoMember(4008)]
    public Dictionary<string, string> Metadata { get; set; } = new();  // Optional metadata for the segment

    // Computed property to help with segment file organization
    public long SegmentFileGroup => SegmentId / 1000;
}
