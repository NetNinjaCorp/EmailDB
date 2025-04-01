namespace EmailDB.Format.Models
{
    /// <summary>
    /// Defines the type of content stored within a block's payload.
    /// Matches the specification in EmailDB_FileFormat_Spec.md.
    /// </summary>
    public enum BlockType : byte
    {
        /// <summary>
        /// Contains global file metadata.
        /// </summary>
        Metadata = 1,

        /// <summary>
        /// Defines the folder hierarchy.
        /// </summary>
        FolderTree = 2,

        /// <summary>
        /// Contains content/references specific to a folder.
        /// </summary>
        FolderContent = 3,

        /// <summary>
        /// Stores a segment of the ZoneTree Key/Value store (e.g., email bodies).
        /// </summary>
        ZoneTreeSegment_KV = 4,

        /// <summary>
        /// Stores a segment of the ZoneTree Vector store (e.g., search indexes).
        /// </summary>
        ZoneTreeSegment_Vector = 5,

        /// <summary>
        /// Marks a block as free/superseded, potentially used during compaction.
        /// </summary>
        FreeSpace = 6,

        // Add other block types as needed, incrementing the byte value.
    }
}