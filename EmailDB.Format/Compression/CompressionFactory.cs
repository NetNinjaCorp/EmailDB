using System;
using System.Collections.Generic;
using EmailDB.Format.Models;

namespace EmailDB.Format.Compression
{
    /// <summary>
    /// Factory for creating compression providers
    /// </summary>
    public static class CompressionFactory
    {
        private static readonly Dictionary<CompressionAlgorithm, Func<ICompressionProvider>> _providers = new()
        {
            { CompressionAlgorithm.None, () => new NoCompressionProvider() },
            { CompressionAlgorithm.Gzip, () => new GzipCompressionProvider() },
            { CompressionAlgorithm.LZ4, () => new LZ4CompressionProvider() },
            { CompressionAlgorithm.Zstd, () => new ZstdCompressionProvider() },
            { CompressionAlgorithm.Brotli, () => new BrotliCompressionProvider() }
        };
        
        /// <summary>
        /// Get a compression provider for the specified algorithm
        /// </summary>
        public static ICompressionProvider GetProvider(CompressionAlgorithm algorithm)
        {
            if (_providers.TryGetValue(algorithm, out var factory))
            {
                return factory();
            }
            
            throw new NotSupportedException($"Compression algorithm {algorithm} is not supported");
        }
        
        /// <summary>
        /// Check if a compression algorithm is supported
        /// </summary>
        public static bool IsSupported(CompressionAlgorithm algorithm)
        {
            return _providers.ContainsKey(algorithm);
        }
    }
}