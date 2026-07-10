using ProtoBuf;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// A single RFC 5322 header field, preserved in the exact order and with the exact raw value it
/// appeared in the message. The full ordered list on <see cref="EmailMetadata.Headers"/> is what
/// lets Tier 2 reconstruct any header-derived Tier 1 field without touching Tier 3.
/// </summary>
[ProtoContract]
public sealed class EmailHeaderField
{
    /// <summary>Header field name (e.g. <c>Subject</c>, <c>From</c>, <c>Received</c>).</summary>
    [ProtoMember(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The header's unfolded value as it appeared in the message.</summary>
    [ProtoMember(2)]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// One node of the email's MIME part tree: the structural shape of the message (content types,
/// dispositions, attachment names and sizes) without the part bodies — those live in Tier 3.
/// A leaf part has no <see cref="Children"/>; a multipart/* container nests its parts.
/// </summary>
[ProtoContract]
public sealed class MimePartNode
{
    /// <summary>MIME media type, e.g. <c>text/plain</c>, <c>text/html</c>, <c>multipart/mixed</c>.</summary>
    [ProtoMember(1)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Content-Disposition (<c>inline</c> / <c>attachment</c>), or empty if unset.</summary>
    [ProtoMember(2)]
    public string Disposition { get; set; } = string.Empty;

    /// <summary>Attachment/inline file name, or empty if none.</summary>
    [ProtoMember(3)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Content-Id of the part (used to resolve inline images), or empty.</summary>
    [ProtoMember(4)]
    public string ContentId { get; set; } = string.Empty;

    /// <summary>Decoded size in bytes of this part's body (0 for containers / when unknown).</summary>
    [ProtoMember(5)]
    public long Size { get; set; }

    /// <summary>Child parts for a multipart container; empty for a leaf part.</summary>
    [ProtoMember(6)]
    public List<MimePartNode> Children { get; set; } = new();
}

/// <summary>
/// Tier 2 email block payload (BlockType 10, docs/Folder_Listing.md Section 1): everything needed
/// to open and thread an email without reading Tier 3 — the full RFC 5322 header set, the MIME
/// part structure, threading references, and the <see cref="Preview"/>.
///
/// <para><b>Tier 1 regenerability.</b> Every FolderPage (Tier 1) field — From, Subject, Date,
/// size — is derivable from <see cref="Headers"/>, and the ~200-char body preview is carried in
/// <see cref="Preview"/>, so a lost folder listing can be rebuilt from Tier 2 alone; recovery
/// never reads Tier 3 (docs/Folder_Listing.md Section 4).</para>
///
/// <para>Stored via <see cref="EmailBlockStore"/>: this protobuf payload is Zstd-compressed and
/// encrypted under the Default policy before being appended as a BlockType 10 block.</para>
/// </summary>
[ProtoContract]
public sealed class EmailMetadata
{
    /// <summary>
    /// The 32-byte <see cref="EmailDB.Format.V3.EmailHashedID"/> of the email — the same content
    /// identity as the Tier 3 <see cref="EmailContent"/> this metadata was derived from.
    /// </summary>
    [ProtoMember(1)]
    public byte[] EmailHashedId { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// BlockId (ULID, 16 bytes) of the Tier 3 <see cref="EmailContent"/> block holding the raw
    /// MIME, or empty if not yet linked. Lets a reader jump from Tier 2 to Tier 3.
    /// </summary>
    [ProtoMember(2)]
    public byte[] ContentBlockId { get; set; } = Array.Empty<byte>();

    /// <summary>The full ordered RFC 5322 header set (see <see cref="EmailHeaderField"/>).</summary>
    [ProtoMember(3)]
    public List<EmailHeaderField> Headers { get; set; } = new();

    /// <summary>Root of the MIME part tree (structure only; bodies live in Tier 3).</summary>
    [ProtoMember(4)]
    public MimePartNode? MimeStructure { get; set; }

    /// <summary>Message-Id of this email (threading identity), or empty.</summary>
    [ProtoMember(5)]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>In-Reply-To message id (threading parent), or empty.</summary>
    [ProtoMember(6)]
    public string InReplyTo { get; set; } = string.Empty;

    /// <summary>References header message ids, oldest-first (threading ancestry).</summary>
    [ProtoMember(7)]
    public List<string> References { get; set; } = new();

    /// <summary>
    /// ~200 chars of plain-text body (<see cref="PreviewExtractor.MaxLength"/>), extracted from the
    /// text or HTML body. Present so Tier 1 listings can be regenerated from Tier 2 alone.
    /// </summary>
    [ProtoMember(8)]
    public string Preview { get; set; } = string.Empty;

    /// <summary>Decoded Subject header, for direct Tier 1 regeneration.</summary>
    [ProtoMember(9)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>Decoded From display value, for direct Tier 1 regeneration.</summary>
    [ProtoMember(10)]
    public string From { get; set; } = string.Empty;

    /// <summary>Send date as UTC ticks (0 if the message had no parseable Date).</summary>
    [ProtoMember(11)]
    public long DateTicks { get; set; }

    /// <summary>Total size in bytes of the raw MIME message (Tier 1 MessageSize).</summary>
    [ProtoMember(12)]
    public long MessageSize { get; set; }
}
