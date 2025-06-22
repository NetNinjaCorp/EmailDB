using System;
using System.IO.Compression;
using EmailDB.Format.Models;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// Brotli compression provider using .NET built-in Brotli
    /// </summary>
    public class BrotliCompressionProvider : ICompressionProvider
    {
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Brotli;
        
        public byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();
                
            var maxCompressedSize = GetMaxCompressedSize(data.Length);
            var compressed = new byte[maxCompressedSize];
            
            if (BrotliEncoder.TryCompress(data, compressed, out var bytesWritten))
            {
                // Return only the actual compressed data
                var result = new byte[bytesWritten];
                Array.Copy(compressed, 0, result, 0, bytesWritten);
                return result;
            }
            
            throw new InvalidOperationException("Brotli compression failed");
        }
        
        public byte[] Compress(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return Array.Empty<byte>();
                
            var maxCompressedSize = GetMaxCompressedSize(data.Length);
            var compressed = new byte[maxCompressedSize];
            
            if (BrotliEncoder.TryCompress(data, compressed, out var bytesWritten))
            {
                // Return only the actual compressed data
                var result = new byte[bytesWritten];
                Array.Copy(compressed, 0, result, 0, bytesWritten);
                return result;
            }
            
            throw new InvalidOperationException("Brotli compression failed");
        }
        
        public byte[] Decompress(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return Array.Empty<byte>();
                
            // Estimate decompressed size (start with 4x compressed size)
            var buffer = new byte[compressedData.Length * 4];
            
            while (true)
            {
                if (BrotliDecoder.TryDecompress(compressedData, buffer, out var bytesWritten))
                {
                    // Return only the actual decompressed data
                    var result = new byte[bytesWritten];
                    Array.Copy(buffer, 0, result, 0, bytesWritten);
                    return result;
                }
                
                // If buffer was too small, double it and try again
                if (buffer.Length >= compressedData.Length * 100) // Prevent infinite loop
                    throw new InvalidOperationException("Brotli decompression failed - output too large");
                    
                buffer = new byte[buffer.Length * 2];
            }
        }
        
        public byte[] Decompress(ReadOnlySpan<byte> compressedData)
        {
            if (compressedData.Length == 0)
                return Array.Empty<byte>();
                
            // Estimate decompressed size (start with 4x compressed size)
            var buffer = new byte[compressedData.Length * 4];
            
            while (true)
            {
                if (BrotliDecoder.TryDecompress(compressedData, buffer, out var bytesWritten))
                {
                    // Return only the actual decompressed data
                    var result = new byte[bytesWritten];
                    Array.Copy(buffer, 0, result, 0, bytesWritten);
                    return result;
                }
                
                // If buffer was too small, double it and try again
                if (buffer.Length >= compressedData.Length * 100) // Prevent infinite loop
                    throw new InvalidOperationException("Brotli decompression failed - output too large");
                    
                buffer = new byte[buffer.Length * 2];
            }
        }
        
        public int GetMaxCompressedSize(int uncompressedSize)
        {
            // Brotli worst case is slightly larger than input
            // This is a conservative estimate
            return uncompressedSize + 32 + (uncompressedSize / 100);
        }
    }
}