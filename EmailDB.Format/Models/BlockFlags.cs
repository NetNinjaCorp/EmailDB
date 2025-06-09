using System;

namespace EmailDB.Format.Models;

[Flags]
public enum BlockFlags : uint
{
    None            = 0x00000000,
    
    // Compression (bits 0-7)
    Compressed      = 0x00000001,
    CompressionMask = 0x000000FE,  // 7 bits for algorithm ID
    
    // Encryption (bits 8-15)
    Encrypted       = 0x00000100,
    EncryptionMask  = 0x0000FE00,  // 7 bits for algorithm ID
    
    // Reserved for future use
    Reserved        = 0xFFFF0000
}

public enum CompressionAlgorithm : byte
{
    None = 0,
    Gzip = 1,
    LZ4 = 2,
    Zstd = 3,
    Brotli = 4
}

public enum EncryptionAlgorithm : byte
{
    None = 0,
    AES256_GCM = 1,
    ChaCha20_Poly1305 = 2,
    AES256_CBC_HMAC = 3
}

public static class BlockFlagsExtensions
{
    public static CompressionAlgorithm GetCompressionAlgorithm(this BlockFlags flags)
    {
        if ((flags & BlockFlags.Compressed) == 0)
            return CompressionAlgorithm.None;
            
        var id = (byte)((uint)(flags & BlockFlags.CompressionMask) >> 1);
        return (CompressionAlgorithm)id;
    }
    
    public static EncryptionAlgorithm GetEncryptionAlgorithm(this BlockFlags flags)
    {
        if ((flags & BlockFlags.Encrypted) == 0)
            return EncryptionAlgorithm.None;
            
        var id = (byte)((uint)(flags & BlockFlags.EncryptionMask) >> 9);
        return (EncryptionAlgorithm)id;
    }
    
    public static BlockFlags SetCompressionAlgorithm(
        this BlockFlags flags, 
        CompressionAlgorithm algorithm)
    {
        if (algorithm == CompressionAlgorithm.None)
            return flags & ~(BlockFlags.Compressed | BlockFlags.CompressionMask);
            
        flags |= BlockFlags.Compressed;
        flags &= ~BlockFlags.CompressionMask;
        flags |= (BlockFlags)((byte)algorithm << 1);
        return flags;
    }
    
    public static BlockFlags SetEncryptionAlgorithm(
        this BlockFlags flags, 
        EncryptionAlgorithm algorithm)
    {
        if (algorithm == EncryptionAlgorithm.None)
            return flags & ~(BlockFlags.Encrypted | BlockFlags.EncryptionMask);
            
        flags |= BlockFlags.Encrypted;
        flags &= ~BlockFlags.EncryptionMask;
        flags |= (BlockFlags)((byte)algorithm << 9);
        return flags;
    }
}