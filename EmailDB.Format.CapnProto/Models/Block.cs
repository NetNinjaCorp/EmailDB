using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.CapnProto.Models;

public class Block
{
    // Header fields.
    public ushort Version { get; set; }
    public BlockType Type { get; set; }
    public byte Flags { get; set; }
    public long Timestamp { get; set; }
    public long BlockId { get; set; }
    public long PayloadLength { get; set; }  // Computed from the payload length.

    // The payload (e.g. Protobuf-encoded data).
    public byte[] Payload { get; set; }

    // Checksums.
    public uint HeaderChecksum { get; set; }
    public uint PayloadChecksum { get; set; }
}
