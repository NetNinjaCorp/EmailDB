using System;

namespace EmailDB.Format.Models // Updated namespace
{
    /// <summary>
    /// Represents the logical content of a block, independent of its on-disk representation.
    /// </summary>
    public class Block
    {
        // Header fields relevant to the logical block content.
        public ushort Version { get; set; } // Block *format* version
        public BlockType Type { get; set; }
        public byte Flags { get; set; }
        public PayloadEncoding PayloadEncoding { get; set; } // Added
        public long Timestamp { get; set; } // UTC Ticks
        public long BlockId { get; set; } // Unique ID for this block instance

        // The actual payload data. Serialization/deserialization is handled elsewhere.
        public byte[] Payload { get; set; }

        // Note: PayloadLength, HeaderChecksum, PayloadChecksum are part of the
        // raw on-disk format handled by RawBlockManager, not this logical model.
    }
}