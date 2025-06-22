using System;
using EmailDB.Format.Models;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// No compression provider (passthrough)
    /// </summary>
    public class NoCompressionProvider : ICompressionProvider
    {
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.None;
        
        public byte[] Compress(byte[] data)
        {
            return data ?? Array.Empty<byte>();
        }
        
        public byte[] Compress(ReadOnlySpan<byte> data)
        {
            return data.ToArray();
        }
        
        public byte[] Decompress(byte[] compressedData)
        {
            return compressedData ?? Array.Empty<byte>();
        }
        
        public byte[] Decompress(ReadOnlySpan<byte> compressedData)
        {
            return compressedData.ToArray();
        }
        
        public int GetMaxCompressedSize(int uncompressedSize)
        {
            return uncompressedSize;
        }
    }
}