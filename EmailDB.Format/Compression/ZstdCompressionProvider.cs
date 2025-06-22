using System;
using EmailDB.Format.Models;
using ZstdSharp;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// Zstd compression provider
    /// </summary>
    public class ZstdCompressionProvider : ICompressionProvider
    {
        private static readonly Compressor _compressor = new Compressor();
        private static readonly Decompressor _decompressor = new Decompressor();
        
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Zstd;
        
        public byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();
                
            return _compressor.Wrap(data).ToArray();
        }
        
        public byte[] Compress(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return Array.Empty<byte>();
                
            return _compressor.Wrap(data).ToArray();
        }
        
        public byte[] Decompress(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return Array.Empty<byte>();
                
            return _decompressor.Unwrap(compressedData).ToArray();
        }
        
        public byte[] Decompress(ReadOnlySpan<byte> compressedData)
        {
            if (compressedData.Length == 0)
                return Array.Empty<byte>();
                
            return _decompressor.Unwrap(compressedData).ToArray();
        }
        
        public int GetMaxCompressedSize(int uncompressedSize)
        {
            // Zstd worst case bound
            return Compressor.GetCompressBound(uncompressedSize);
        }
    }
}