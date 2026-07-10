using ProtoBuf;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// Tier 3 email block payload (BlockType 7, docs/Folder_Listing.md Section 1): the raw MIME
/// body — headers, inline images and attachments exactly as received. This is the coldest,
/// largest tier and is read only when a body or attachment is actually viewed.
///
/// <para><b>Content identity.</b> <see cref="RawContent"/> is the canonical content bytes: the
/// <see cref="EmailDB.Format.V3.EmailHashedID"/> of the email is the SHA3-256 of exactly these
/// bytes. <see cref="EmailHashedId"/> caches that 32-byte digest so a reader has the identity
/// without re-hashing; it is always recomputable from <see cref="RawContent"/> and never feeds
/// the hash itself (the hash input is the raw bytes, never this parsed model — see
/// <see cref="EmailDB.Format.V3.EmailHashedID"/>).</para>
///
/// <para>Stored via <see cref="EmailBlockStore"/>, which serializes this protobuf payload, then
/// compresses it with Zstd and encrypts it under the Default policy before appending it as a
/// BlockType 7 block.</para>
/// </summary>
[ProtoContract]
public sealed class EmailContent
{
    /// <summary>
    /// The 32-byte SHA3-256 content identity (<see cref="EmailDB.Format.V3.EmailHashedID"/>) of
    /// <see cref="RawContent"/>. A cached copy of the digest; the authoritative source is always
    /// the raw bytes, so a reader can verify this by re-hashing <see cref="RawContent"/>.
    /// </summary>
    [ProtoMember(1)]
    public byte[] EmailHashedId { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The raw RFC 5322 MIME message octets exactly as received/stored — the canonical content
    /// bytes that the <see cref="EmailDB.Format.V3.EmailHashedID"/> hashes and that Tier 2's
    /// <see cref="EmailMetadata"/> is derived from.
    /// </summary>
    [ProtoMember(2)]
    public byte[] RawContent { get; set; } = Array.Empty<byte>();
}
