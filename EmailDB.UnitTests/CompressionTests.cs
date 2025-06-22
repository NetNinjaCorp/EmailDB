using System;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using EmailDB.Format.Compression;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests
{
    public class CompressionTests
    {
        private readonly ITestOutputHelper _output;

        public CompressionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void CompressionFactory_Should_Create_All_Providers(CompressionAlgorithm algorithm)
        {
            // Act
            var provider = CompressionFactory.GetProvider(algorithm);

            // Assert
            Assert.NotNull(provider);
            Assert.Equal(algorithm, provider.Algorithm);
        }

        [Fact]
        public void CompressionFactory_Should_Throw_For_Unsupported_Algorithm()
        {
            // Arrange
            var unsupportedAlgorithm = (CompressionAlgorithm)99;

            // Act & Assert
            Assert.Throws<NotSupportedException>(() => CompressionFactory.GetProvider(unsupportedAlgorithm));
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void All_Algorithms_Should_Support_Empty_Data(CompressionAlgorithm algorithm)
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
        public void All_Algorithms_Should_Roundtrip_Text_Data(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var originalText = "Hello, World! This is a test of compression algorithms. " +
                              "This text should compress well due to repetition. " +
                              "Compression algorithms work by finding patterns in data. " +
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

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void All_Algorithms_Should_Roundtrip_Binary_Data(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var random = new Random(42); // Fixed seed for reproducibility
            var originalData = new byte[1024];
            random.NextBytes(originalData);

            // Act
            var compressed = provider.Compress(originalData);
            var decompressed = provider.Decompress(compressed);

            // Assert
            Assert.Equal(originalData, decompressed);

            // Log compression ratio
            var ratio = (double)compressed.Length / originalData.Length;
            _output.WriteLine($"{algorithm} (binary): Original={originalData.Length}, Compressed={compressed.Length}, Ratio={ratio:F3}");
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void All_Algorithms_Should_Handle_Large_Data(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var largeText = string.Join("\n", Enumerable.Repeat("This is a line of text that will be repeated many times to create large data.", 1000));
            var originalData = Encoding.UTF8.GetBytes(largeText);

            // Act
            var compressed = provider.Compress(originalData);
            var decompressed = provider.Decompress(compressed);

            // Assert
            Assert.Equal(originalData, decompressed);

            // Log compression ratio for large data
            var ratio = (double)compressed.Length / originalData.Length;
            _output.WriteLine($"{algorithm} (large): Original={originalData.Length}, Compressed={compressed.Length}, Ratio={ratio:F3}");

            // For repetitive data, compression should be effective (except for None)
            if (algorithm != CompressionAlgorithm.None)
            {
                Assert.True(ratio < 0.5, $"{algorithm} should achieve better than 50% compression on repetitive data");
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

            // Assert
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
        public void All_Algorithms_Should_Support_Span_Operations(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var originalText = "Test data for span operations";
            var originalData = Encoding.UTF8.GetBytes(originalText);
            ReadOnlySpan<byte> dataSpan = originalData;

            // Act
            var compressed = provider.Compress(dataSpan);
            ReadOnlySpan<byte> compressedSpan = compressed;
            var decompressed = provider.Decompress(compressedSpan);
            var decompressedText = Encoding.UTF8.GetString(decompressed);

            // Assert
            Assert.Equal(originalText, decompressedText);
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None, 100)]
        [InlineData(CompressionAlgorithm.Gzip, 100)]
        [InlineData(CompressionAlgorithm.LZ4, 100)]
        [InlineData(CompressionAlgorithm.Zstd, 100)]
        [InlineData(CompressionAlgorithm.Brotli, 100)]
        public void GetMaxCompressedSize_Should_Return_Reasonable_Bounds(CompressionAlgorithm algorithm, int inputSize)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);

            // Act
            var maxSize = provider.GetMaxCompressedSize(inputSize);

            // Assert
            Assert.True(maxSize >= inputSize, "Max compressed size should be at least the input size");
            
            if (algorithm == CompressionAlgorithm.None)
            {
                Assert.Equal(inputSize, maxSize);
            }
            else
            {
                // Should provide a reasonable upper bound (not more than 2x for most algorithms)
                Assert.True(maxSize <= inputSize * 2, $"Max compressed size seems unreasonably large for {algorithm}");
            }
        }

        [Fact]
        public void NoCompressionProvider_Should_Be_Passthrough()
        {
            // Arrange
            var provider = new NoCompressionProvider();
            var testData = Encoding.UTF8.GetBytes("Test data");

            // Act
            var compressed = provider.Compress(testData);
            var decompressed = provider.Decompress(compressed);

            // Assert
            Assert.Same(testData, compressed); // Should return the same reference
            Assert.Same(compressed, decompressed); // Should return the same reference
            Assert.Equal(CompressionAlgorithm.None, provider.Algorithm);
        }

        [Theory]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void Compression_Should_Handle_Null_Input_Gracefully(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);

            // Act
            var compressed = provider.Compress((byte[])null);
            var decompressed = provider.Decompress((byte[])null);

            // Assert
            Assert.NotNull(compressed);
            Assert.NotNull(decompressed);
            Assert.Empty(compressed);
            Assert.Empty(decompressed);
        }

        [Theory]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public void Compression_Should_Handle_Single_Byte(CompressionAlgorithm algorithm)
        {
            // Arrange
            var provider = CompressionFactory.GetProvider(algorithm);
            var singleByte = new byte[] { 42 };

            // Act
            var compressed = provider.Compress(singleByte);
            var decompressed = provider.Decompress(compressed);

            // Assert
            Assert.Equal(singleByte, decompressed);
        }
    }
}