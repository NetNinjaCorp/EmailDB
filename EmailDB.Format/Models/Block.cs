using EmailDB.Format.Models.BlockTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;

public class Block
{
    // Header fields.
    public ushort Version { get; set; } = 1;  // Default version
    public BlockType Type { get; set; }
    public byte Flags { get; set; }
    public PayloadEncoding Encoding { get; set; } = PayloadEncoding.RawBytes;  // Default encoding
    public long Timestamp { get; set; }
    public long BlockId { get; set; }
    public long PayloadLength { get; set; }  // Computed from the payload length.

    // The payload (e.g. Protobuf-encoded data).
    public byte[] Payload { get; set; } = Array.Empty<byte>();  // Default empty array

    // Checksums.
    public uint HeaderChecksum { get; set; }
    public uint PayloadChecksum { get; set; }

}
