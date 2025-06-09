using System;
using System.IO;

namespace EmailDB.Format.Models;

public class ExtendedBlockHeader
{
    // Compression fields
    public long UncompressedSize { get; set; }
    
    // Encryption fields
    public byte[] IV { get; set; }
    public byte[] AuthTag { get; set; }
    public int KeyId { get; set; }
    
    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Write header version
        writer.Write((byte)1);
        
        // Write uncompressed size if present
        if (UncompressedSize > 0)
        {
            writer.Write(true);
            writer.Write(UncompressedSize);
        }
        else
        {
            writer.Write(false);
        }
        
        // Write encryption data if present
        if (IV != null && IV.Length > 0)
        {
            writer.Write(true);
            writer.Write(IV.Length);
            writer.Write(IV);
            writer.Write(AuthTag.Length);
            writer.Write(AuthTag);
            writer.Write(KeyId);
        }
        else
        {
            writer.Write(false);
        }
        
        return ms.ToArray();
    }
    
    public static ExtendedBlockHeader Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        
        var header = new ExtendedBlockHeader();
        
        // Read version
        var version = reader.ReadByte();
        
        // Read uncompressed size
        if (reader.ReadBoolean())
        {
            header.UncompressedSize = reader.ReadInt64();
        }
        
        // Read encryption data
        if (reader.ReadBoolean())
        {
            var ivLength = reader.ReadInt32();
            header.IV = reader.ReadBytes(ivLength);
            var authTagLength = reader.ReadInt32();
            header.AuthTag = reader.ReadBytes(authTagLength);
            header.KeyId = reader.ReadInt32();
        }
        
        return header;
    }
}