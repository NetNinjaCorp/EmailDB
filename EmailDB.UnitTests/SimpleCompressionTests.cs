using System;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using EmailDB.Format.Compression;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests
{
    public class SimpleCompressionTests
    {
        private readonly ITestOutputHelper _output;

        public SimpleCompressionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void All_Compression_Algorithms_Should_Work(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var originalText = "This is test data that should compress well. " +
                              "Compression algorithms work by finding patterns. " +
                              "The more patterns, the better the compression ratio.";
            var originalData = Encoding.UTF8.GetBytes(originalText);

            // Act
            var compressed = provider.Compress(originalData);
            var decompressed = provider.Decompress(compressed);
            var decompressedText = Encoding.UTF8.GetString(decompressed);

            // Assert
            Assert.Equal(originalText, decompressedText);
            Assert.Equal(originalData, decompressed);

            // Log compression ratio
            var ratio = (double)compressed.Length / originalData.Length;
            _output.WriteLine($"{algorithm}: Original={originalData.Length}, Compressed={compressed.Length}, Ratio={ratio:F3}");
        }

        [Fact]
        public void CompressionFactory_Should_Support_All_Algorithms()
        {
            foreach (CompressionAlgorithm algorithm in Enum.GetValues<CompressionAlgorithm>())
            {
                var provider = CompressionFactory.GetProvider(algorithm);
                Assert.NotNull(provider);
                Assert.Equal(algorithm, provider.Algorithm);
                _output.WriteLine($"✓ {algorithm} provider created successfully");
            }
        }

        [Theory]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void Compression_Should_Be_Effective_On_Repetitive_Data(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var repetitiveData = Encoding.UTF8.GetBytes(new string('A', 1000));

            // Act
            var compressed = provider.Compress(repetitiveData);
            var decompressed = provider.Decompress(compressed);

            // Assert
            Assert.Equal(repetitiveData, decompressed);
            
            var ratio = (double)compressed.Length / repetitiveData.Length;
            _output.WriteLine($"{algorithm} (repetitive): Original={repetitiveData.Length}, Compressed={compressed.Length}, Ratio={ratio:F4}");

            // Should achieve very good compression on highly repetitive data
            Assert.True(ratio < 0.1, $"{algorithm} should achieve better than 10% compression on highly repetitive data");
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void Empty_Data_Should_Work_With_All_Algorithms(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var emptyData = Array.Empty<byte>();

            // Act
            var compressed = provider.Compress(emptyData);
            var decompressed = provider.Decompress(compressed);

            // Assert
            Assert.NotNull(compressed);
            Assert.NotNull(decompressed);
            Assert.Empty(decompressed);
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void Block_Flags_Should_Set_Compression_Correctly(CompressionAlgorithm algorithm)
        {
            // Arrange
            var flags = BlockFlags.None;

            // Act
            var updatedFlags = flags.SetCompressionAlgorithm(algorithm);
            var retrievedAlgorithm = updatedFlags.GetCompressionAlgorithm();

            // Assert
            Assert.Equal(algorithm, retrievedAlgorithm);
            
            if (algorithm != CompressionAlgorithm.None)
            {
                Assert.True(updatedFlags.HasFlag(BlockFlags.Compressed));
            }
            else
            {
                Assert.False(updatedFlags.HasFlag(BlockFlags.Compressed));
            }
        }
    }
}