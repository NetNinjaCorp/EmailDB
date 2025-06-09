using System;
using System.Collections.Generic;
using ProtoBuf;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.Models.BlockTypes;

[ProtoContract]
public class FolderEnvelopeBlock : BlockContent
{
    [ProtoMember(1)]
    public string FolderPath { get; set; }
    
    [ProtoMember(2)]
    public int Version { get; set; }
    
    [ProtoMember(3)]
    public List<EmailEnvelope> Envelopes { get; set; } = new();
    
    [ProtoMember(4)]
    public DateTime LastModified { get; set; }
    
    [ProtoMember(5)]
    public long? PreviousBlockId { get; set; } // For versioning
    
    public override BlockType GetBlockType() => BlockType.FolderEnvelope;
}