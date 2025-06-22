using System;
using EmailDB.Format.Models;
using K4os.Compression.LZ4;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// LZ4 compression provider
    /// </summary>
    public class LZ4CompressionProvider : ICompressionProvider
    {
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.LZ4;
        
        public byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();
                
            var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
            var compressed = new byte[maxCompressedSize];
            
            var compressedSize = LZ4Codec.Encode(
                data, 0, data.Length,
                compressed, 0, compressed.Length,
                LZ4Level.L09_HC);
                
            if (compressedSize <= 0)
                throw new InvalidOperationException("LZ4 compression failed");
                
            // Return only the actual compressed data
            var result = new byte[compressedSize];
            Array.Copy(compressed, 0, result, 0, compressedSize);
            return result;
        }
        
        public byte[] Compress(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return Array.Empty<byte>();
                
            var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
            var compressed = new byte[maxCompressedSize];
            
            var compressedSize = LZ4Codec.Encode(
                data,
                compressed.AsSpan(),
                LZ4Level.L09_HC);
                
            if (compressedSize <= 0)
                throw new InvalidOperationException("LZ4 compression failed");
                
            // Return only the actual compressed data
            var result = new byte[compressedSize];
            Array.Copy(compressed, 0, result, 0, compressedSize);
            return result;
        }
        
        public byte[] Decompress(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return Array.Empty<byte>();
                
            // For decompression, we need to know the original size
            // In our block format, this comes from the ExtendedBlockHeader
            // For now, we'll use a reasonable buffer and resize as needed
            var buffer = new byte[compressedData.Length * 4]; // Start with 4x size
            
            var decompressedSize = LZ4Codec.Decode(
                compressedData, 0, compressedData.Length,
                buffer, 0, buffer.Length);
                
            if (decompressedSize <= 0)
                throw new InvalidOperationException("LZ4 decompression failed");
                
            // Return only the actual decompressed data
            var result = new byte[decompressedSize];
            Array.Copy(buffer, 0, result, 0, decompressedSize);
            return result;
        }
        
        public byte[] Decompress(ReadOnlySpan<byte> compressedData)
        {
            if (compressedData.Length == 0)
                return Array.Empty<byte>();
                
            // For decompression, we need to know the original size
            var buffer = new byte[compressedData.Length * 4]; // Start with 4x size
            
            var decompressedSize = LZ4Codec.Decode(
                compressedData,
                buffer.AsSpan());
                
            if (decompressedSize <= 0)
                throw new InvalidOperationException("LZ4 decompression failed");
                
            // Return only the actual decompressed data
            var result = new byte[decompressedSize];
            Array.Copy(buffer, 0, result, 0, decompressedSize);
            return result;
        }
        
        public int GetMaxCompressedSize(int uncompressedSize)
        {
            return LZ4Codec.MaximumOutputSize(uncompressedSize);
        }
    }
}