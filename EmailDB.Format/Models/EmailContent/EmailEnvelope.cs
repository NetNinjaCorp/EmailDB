using System;
using ProtoBuf;

namespace EmailDB.Format.Models.EmailContent;

[ProtoContract]
public class EmailEnvelope
{
    [ProtoMember(1)]
    public string CompoundId { get; set; } // BlockId:LocalId
    
    [ProtoMember(2)]
    public string MessageId { get; set; }
    
    [ProtoMember(3)]
    public string Subject { get; set; }
    
    [ProtoMember(4)]
    public string From { get; set; }
    
    [ProtoMember(5)]
    public string To { get; set; }
    
    [ProtoMember(6)]
    public DateTime Date { get; set; }
    
    [ProtoMember(7)]
    public long Size { get; set; }
    
    [ProtoMember(8)]
    public bool HasAttachments { get; set; }
    
    [ProtoMember(9)]
    public int Flags { get; set; } // Read, Flagged, etc.
    
    [ProtoMember(10)]
    public byte[] EnvelopeHash { get; set; }
}