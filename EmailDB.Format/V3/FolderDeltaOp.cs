namespace EmailDB.Format.V3;

/// <summary>
/// The mutation kind carried by a <see cref="FolderDeltaEntry"/> in a <see cref="FolderDeltaLog"/>
/// block (BlockType 13, docs/Folder_Listing.md Sections 2-3). Values start at 1 so a zero op byte
/// is an unambiguous "unset/corrupt" sentinel the unpacker can reject.
/// </summary>
public enum FolderDeltaOp : byte
{
    /// <summary>An email joins the folder; the entry carries the full listing record to merge into a page.</summary>
    Add = 1,

    /// <summary>An email leaves the folder; the entry carries only the EmailHashedID to drop.</summary>
    Delete = 2,

    /// <summary>An email's listing flags change; the entry carries the EmailHashedID and the new flags.</summary>
    FlagChange = 3,
}
