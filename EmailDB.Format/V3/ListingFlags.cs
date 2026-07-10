namespace EmailDB.Format.V3;

/// <summary>
/// Per-email state flags packed into a <see cref="ListingRecord"/>'s 4-byte Flags field
/// (docs/Folder_Listing.md Section 2). These are folder-listing state bits — not content — so a
/// listing regenerated from Tier 2 during recovery resets them to <see cref="None"/>
/// (docs/Folder_Listing.md Section 4).
/// </summary>
[Flags]
public enum ListingFlags : uint
{
    /// <summary>No flags set (the recovery default).</summary>
    None = 0,

    /// <summary>The message has been read/seen.</summary>
    Read = 1 << 0,

    /// <summary>The message is flagged/starred.</summary>
    Flagged = 1 << 1,

    /// <summary>The message has been replied to.</summary>
    Answered = 1 << 2,

    /// <summary>The message is a draft.</summary>
    Draft = 1 << 3,
}
