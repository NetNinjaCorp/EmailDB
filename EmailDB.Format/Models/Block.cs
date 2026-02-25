using EmailDB.Format.Models.BlockTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models;

public class Block
{
    // Flag bit constants
    /// <summary>Bit 0: payload is encrypted (0 = plaintext, 1 = encrypted).</summary>
    public const byte FlagEncrypted = 0x01;

    // Header fields.
    public ushort Version { get; set; }
    public BlockType Type { get; set; }
    public byte Flags { get; set; }
    public long Timestamp { get; set; }
    public long BlockId { get; set; }
    public long PayloadLength { get; set; }  // Computed from the payload length.

    // The payload (e.g. Protobuf-encoded data).
    public byte[] Payload { get; set; }

    // Checksums (16-byte BLAKE3-128).
    public byte[] HeaderChecksum { get; set; }
    public byte[] PayloadChecksum { get; set; }

    /// <summary>True if the payload is encrypted (Flags bit 0 set).</summary>
    public bool IsEncrypted => (Flags & FlagEncrypted) != 0;

    /// <summary>Key epoch stored in bits 1-7 of the Flags byte (0-127).</summary>
    public byte KeyEpoch => (byte)((Flags >> 1) & 0x7F);

    /// <summary>
    /// Writes <paramref name="epoch"/> (0-127) into bits 1-7 of <see cref="Flags"/>,
    /// preserving bit 0 (the encrypted flag).
    /// </summary>
    public void SetKeyEpoch(byte epoch)
    {
        if (epoch > 127)
            throw new ArgumentOutOfRangeException(nameof(epoch), epoch, "Key epoch must be 0-127.");
        Flags = (byte)((Flags & 0x01) | (epoch << 1));
    }

}
