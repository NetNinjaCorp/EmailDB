using System;
using System.IO;
using System.IO.Compression;
using EmailDB.Format.Models;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// Gzip compression provider
    /// </summary>
    public class GzipCompressionProvider : ICompressionProvider
    {
        public CompressionAlgorithm Algorithm => CompressionAlgorithm.Gzip;
        
        public byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
                return Array.Empty<byte>();
                
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }
        
        public byte[] Compress(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return Array.Empty<byte>();
                
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(data);
            }
            return output.ToArray();
        }
        
        public byte[] Decompress(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return Array.Empty<byte>();
                
            using var input = new MemoryStream(compressedData);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            
            gzip.CopyTo(output);
            return output.ToArray();
        }
        
        public byte[] Decompress(ReadOnlySpan<byte> compressedData)
        {
            if (compressedData.Length == 0)
                return Array.Empty<byte>();
                
            using var input = new MemoryStream(compressedData.ToArray());
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            
            gzip.CopyTo(output);
            return output.ToArray();
        }
        
        public int GetMaxCompressedSize(int uncompressedSize)
        {
            // Gzip worst case is slightly larger than input
            // Header (10 bytes) + deflate overhead + trailer (8 bytes)
            return uncompressedSize + 18 + (uncompressedSize / 1000);
        }
    }
}