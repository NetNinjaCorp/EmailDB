using EmailDB.Format.V3;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// Builds a Tier 1 <see cref="ListingRecord"/> from a Tier 2 <see cref="EmailMetadata"/> block — the
/// listing-regeneration path of docs/Folder_Listing.md Section 4, where a lost or corrupt folder
/// page is rebuilt from Tier 2 alone (Tier 3 is never read). Every packed field
/// (EmailHashedID, ContentBlockId, date, size, From, Subject, Preview) comes straight from the
/// metadata; only the folder-state <see cref="ListingFlags"/> are not derivable from content and so
/// default to <see cref="ListingFlags.None"/> unless the caller supplies the live flags.
/// </summary>
public static class ListingRecordBuilder
{
    /// <summary>
    /// Projects <paramref name="metadata"/> to a listing record. <paramref name="flags"/> carries the
    /// live per-folder state (read/flagged/…); it defaults to <see cref="ListingFlags.None"/>, the
    /// recovery reset value.
    /// </summary>
    public static ListingRecord FromMetadata(EmailMetadata metadata, ListingFlags flags = ListingFlags.None)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var contentBlockId = metadata.ContentBlockId is not null
            && metadata.ContentBlockId.Length == UlidGenerator.UlidSize
                ? (byte[])metadata.ContentBlockId.Clone()
                : new byte[UlidGenerator.UlidSize];

        return new ListingRecord
        {
            EmailHashedId = metadata.EmailHashedId is not null
                && metadata.EmailHashedId.Length == EmailDB.Format.V3.EmailHashedID.Size
                    ? new EmailDB.Format.V3.EmailHashedID(metadata.EmailHashedId)
                    : EmailDB.Format.V3.EmailHashedID.Empty,
            ContentBlockId = contentBlockId,
            DateTicks = metadata.DateTicks,
            Flags = flags,
            MessageSize = metadata.MessageSize,
            From = metadata.From ?? string.Empty,
            Subject = metadata.Subject ?? string.Empty,
            Preview = metadata.Preview ?? string.Empty,
        };
    }
}
