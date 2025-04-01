namespace EmailDB.Format.Models
{
    /// <summary>
    /// Represents the position and length of a block within the data file.
    /// </summary>
    public struct BlockLocation
    {
        /// <summary>
        /// The starting byte offset of the block in the file.
        /// </summary>
        public long Position { get; set; }

        /// <summary>
        /// The total length of the block in bytes (including header, payload, checksums, footer).
        /// </summary>
        public long Length { get; set; }
    }
}