using ProtoBuf;
using System;
using System.Collections.Generic;

namespace EmailDB.Format.Models
{
    /// <summary>
    /// Protobuf-compatible email content model
    /// </summary>
    [ProtoContract]
    public class ProtoEmailContent
    {
        [ProtoMember(1)]
        public string MessageId { get; set; } = "";
        
        [ProtoMember(2)]
        public string Subject { get; set; } = "";
        
        [ProtoMember(3)]
        public string From { get; set; } = "";
        
        [ProtoMember(4)]
        public string To { get; set; } = "";
        
        [ProtoMember(5)]
        public DateTime Date { get; set; }
        
        [ProtoMember(6)]
        public string TextBody { get; set; } = "";
        
        [ProtoMember(7)]
        public string HtmlBody { get; set; } = "";
        
        [ProtoMember(8)]
        public long Size { get; set; }
        
        [ProtoMember(9)]
        public string? FileName { get; set; }
    }

    /// <summary>
    /// Protobuf-compatible email metadata model
    /// </summary>
    [ProtoContract]
    public class ProtoEmailMetadata
    {
        [ProtoMember(1)]
        public string EmailId { get; set; } = "";
        
        [ProtoMember(2)]
        public DateTime ImportDate { get; set; }
        
        [ProtoMember(3)]
        public string? FileName { get; set; }
        
        [ProtoMember(4)]
        public long Size { get; set; }
        
        [ProtoMember(5)]
        public bool HasAttachments { get; set; }
    }

    /// <summary>
    /// Protobuf-compatible string list for folders
    /// </summary>
    [ProtoContract]
    public class ProtoStringList
    {
        [ProtoMember(1)]
        public List<string> Items { get; set; } = new List<string>();
    }

    /// <summary>
    /// Protobuf-compatible email ID list
    /// </summary>
    [ProtoContract]
    public class ProtoEmailIdList
    {
        [ProtoMember(1)]
        public List<string> EmailIds { get; set; } = new List<string>();
    }
}