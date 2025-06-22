using System;
using EmailDB.Format.Models;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// Interface for compression providers
    /// </summary>
    public interface ICompressionProvider
    {
        /// <summary>
        /// The compression algorithm this provider implements
        /// </summary>
        CompressionAlgorithm Algorithm { get; }
        
        /// <summary>
        /// Compress data
        /// </summary>
        /// <param name="data">Data to compress</param>
        /// <returns>Compressed data</returns>
        byte[] Compress(byte[] data);
        
        /// <summary>
        /// Compress data with span support
        /// </summary>
        /// <param name="data">Data to compress</param>
        /// <returns>Compressed data</returns>
        byte[] Compress(ReadOnlySpan<byte> data);
        
        /// <summary>
        /// Decompress data
        /// </summary>
        /// <param name="compressedData">Compressed data</param>
        /// <returns>Decompressed data</returns>
        byte[] Decompress(byte[] compressedData);
        
        /// <summary>
        /// Decompress data with span support
        /// </summary>
        /// <param name="compressedData">Compressed data</param>
        /// <returns>Decompressed data</returns>
        byte[] Decompress(ReadOnlySpan<byte> compressedData);
        
        /// <summary>
        /// Get the maximum compressed size for a given input size
        /// </summary>
        /// <param name="uncompressedSize">Size of uncompressed data</param>
        /// <returns>Maximum possible compressed size</returns>
        int GetMaxCompressedSize(int uncompressedSize);
    }
}