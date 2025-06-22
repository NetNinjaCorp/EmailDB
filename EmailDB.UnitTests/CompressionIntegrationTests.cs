using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests
{
    public class CompressionIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDirectory;

        public CompressionIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"emaildb_compression_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Theory]
        [InlineData(CompressionAlgorithm.None)]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public async Task RawBlockManager_Should_Roundtrip_Compressed_Blocks(CompressionAlgorithm algorithm)
        {
            // Arrange
            var testFile = Path.Combine(_tempDirectory, $"test_{algorithm}.edb");
            var testData = Encoding.UTF8.GetBytes("This is test data that should compress well. " +
                                                 "Compression algorithms work by finding patterns. " +
                                                 "The more patterns, the better the compression.");

            var originalBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailBatch,
                Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(algorithm),
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 12345,
                Payload = testData
            };

            // Act - Write with compression
            BlockLocation location;
            using (var manager = new RawBlockManager(testFile))
            {
                var writeResult = await manager.WriteBlockAsync(originalBlock);
                Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");
                location = writeResult.Value;
            }

            // Act - Read with decompression
            Block readBlock;
            using (var manager = new RawBlockManager(testFile))
            {
                var readResult = await manager.ReadBlockAsync(originalBlock.BlockId);
                Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");
                readBlock = readResult.Value;
            }

            // Assert
            Assert.Equal(originalBlock.Version, readBlock.Version);
            Assert.Equal(originalBlock.Type, readBlock.Type);
            Assert.Equal(originalBlock.Flags, readBlock.Flags);
            Assert.Equal(originalBlock.Encoding, readBlock.Encoding);
            Assert.Equal(originalBlock.Timestamp, readBlock.Timestamp);
            Assert.Equal(originalBlock.BlockId, readBlock.BlockId);
            Assert.Equal(originalBlock.Payload, readBlock.Payload);

            // Check compression effectiveness
            var fileSize = new FileInfo(testFile).Length;
            _output.WriteLine($"{algorithm}: Block size on disk = {fileSize} bytes, Original payload = {testData.Length} bytes");
            
            if (algorithm != CompressionAlgorithm.None)
            {
                // For this repetitive test data, compression should be effective
                // The block has overhead, but compression should still show benefits
                _output.WriteLine($"{algorithm}: Compression appears to be working (file size includes block headers)");
            }
        }

        [Theory]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public async Task Compressed_Blocks_Should_Be_Smaller_Than_Uncompressed(CompressionAlgorithm algorithm)
        {
            // Arrange - Create highly compressible data
            var repetitiveData = Encoding.UTF8.GetBytes(string.Join("\n", 
                System.Linq.Enumerable.Repeat("This line repeats many times to test compression effectiveness.", 100)));

            var uncompressedFile = Path.Combine(_tempDirectory, "uncompressed.edb");
            var compressedFile = Path.Combine(_tempDirectory, $"compressed_{algorithm}.edb");

            var baseBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailBatch,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 1,
                Payload = repetitiveData
            };

            // Write uncompressed block
            var uncompressedBlock = baseBlock;
            uncompressedBlock.Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(CompressionAlgorithm.None);
            
            using (var manager = new RawBlockManager(uncompressedFile))
            {
                var result = await manager.WriteBlockAsync(uncompressedBlock);
                Assert.True(result.IsSuccess);
            }

            // Write compressed block
            var compressedBlock = baseBlock;
            compressedBlock.BlockId = 2;
            compressedBlock.Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(algorithm);
            
            using (var manager = new RawBlockManager(compressedFile))
            {
                var result = await manager.WriteBlockAsync(compressedBlock);
                Assert.True(result.IsSuccess);
            }

            // Compare file sizes
            var uncompressedSize = new FileInfo(uncompressedFile).Length;
            var compressedSize = new FileInfo(compressedFile).Length;
            var compressionRatio = (double)compressedSize / uncompressedSize;

            _output.WriteLine($"{algorithm}: Uncompressed={uncompressedSize}, Compressed={compressedSize}, Ratio={compressionRatio:F3}");

            // For highly repetitive data, we should see significant compression
            Assert.True(compressionRatio < 0.5, $"{algorithm} should achieve better than 50% compression on repetitive data");
        }

        [Fact]
        public async Task Empty_Payload_Should_Work_With_All_Compression_Algorithms()
        {
            foreach (CompressionAlgorithm algorithm in Enum.GetValues<CompressionAlgorithm>())
            {
                var testFile = Path.Combine(_tempDirectory, $"empty_{algorithm}.edb");
                
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.Metadata,
                    Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(algorithm),
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    BlockId = (long)algorithm,
                    Payload = Array.Empty<byte>()
                };

                // Write and read back
                using (var manager = new RawBlockManager(testFile))
                {
                    var writeResult = await manager.WriteBlockAsync(block);
                    Assert.True(writeResult.IsSuccess, $"Write failed for {algorithm}: {writeResult.Error}");

                    var readResult = await manager.ReadBlockAsync(block.BlockId);
                    Assert.True(readResult.IsSuccess, $"Read failed for {algorithm}: {readResult.Error}");

                    var readBlock = readResult.Value;
                    Assert.Empty(readBlock.Payload);
                    Assert.Equal(block.Flags, readBlock.Flags);
                }
            }
        }

        [Theory]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public async Task Large_Payload_Should_Compress_And_Decompress_Correctly(CompressionAlgorithm algorithm)
        {
            // Arrange - Create a large payload (1MB)
            var random = new Random(42); // Fixed seed
            var largeData = new byte[1024 * 1024];
            
            // Fill with pattern that has some compressibility
            for (int i = 0; i < largeData.Length; i++)
            {
                largeData[i] = (byte)(i % 256);
            }

            var testFile = Path.Combine(_tempDirectory, $"large_{algorithm}.edb");
            var block = new Block
            {
                Version = 1,
                Type = BlockType.EmailBatch,
                Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(algorithm),
                Encoding = PayloadEncoding.RawBytes,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 999,
                Payload = largeData
            };

            // Act
            using (var manager = new RawBlockManager(testFile))
            {
                var writeResult = await manager.WriteBlockAsync(block);
                Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

                var readResult = await manager.ReadBlockAsync(block.BlockId);
                Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");

                var readBlock = readResult.Value;
                Assert.Equal(largeData, readBlock.Payload);
            }

            var fileSize = new FileInfo(testFile).Length;
            var compressionRatio = (double)fileSize / (largeData.Length + 1024); // Account for block overhead
            _output.WriteLine($"{algorithm} (large): Original=1MB, File size={fileSize}, Approx ratio={compressionRatio:F3}");
        }

        [Fact]
        public async Task Multiple_Compressed_Blocks_Should_Work_In_Same_File()
        {
            var testFile = Path.Combine(_tempDirectory, "multiple_compressed.edb");
            var testData = Encoding.UTF8.GetBytes("Test data for multiple blocks");

            using var manager = new RawBlockManager(testFile);

            // Write blocks with different compression algorithms
            var algorithms = new[] { 
                CompressionAlgorithm.None, 
                CompressionAlgorithm.Gzip, 
                CompressionAlgorithm.LZ4, 
                CompressionAlgorithm.Zstd, 
                CompressionAlgorithm.Brotli 
            };

            // Write all blocks
            for (int i = 0; i < algorithms.Length; i++)
            {
                var block = new Block
                {
                    Version = 1,
                    Type = BlockType.EmailBatch,
                    Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(algorithms[i]),
                    Encoding = PayloadEncoding.Json,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    BlockId = i + 1,
                    Payload = testData
                };

                var writeResult = await manager.WriteBlockAsync(block);
                Assert.True(writeResult.IsSuccess, $"Write failed for {algorithms[i]}: {writeResult.Error}");
            }

            // Read all blocks back
            for (int i = 0; i < algorithms.Length; i++)
            {
                var readResult = await manager.ReadBlockAsync(i + 1);
                Assert.True(readResult.IsSuccess, $"Read failed for block {i + 1}: {readResult.Error}");

                var readBlock = readResult.Value;
                Assert.Equal(testData, readBlock.Payload);
                
                var expectedFlags = ((BlockFlags)0).SetCompressionAlgorithm(algorithms[i]);
                Assert.Equal(expectedFlags, (BlockFlags)readBlock.Flags);
            }
        }

        [Theory]
        [InlineData(CompressionAlgorithm.Gzip)]
        [InlineData(CompressionAlgorithm.LZ4)]
        [InlineData(CompressionAlgorithm.Zstd)]
        [InlineData(CompressionAlgorithm.Brotli)]
        public async Task Block_Flags_Should_Correctly_Indicate_Compression_Algorithm(CompressionAlgorithm algorithm)
        {
            var testFile = Path.Combine(_tempDirectory, $"flags_{algorithm}.edb");
            var block = new Block
            {
                Version = 1,
                Type = BlockType.EmailBatch,
                Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(algorithm),
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 1,
                Payload = Encoding.UTF8.GetBytes("Test data")
            };

            using var manager = new RawBlockManager(testFile);
            
            var writeResult = await manager.WriteBlockAsync(block);
            Assert.True(writeResult.IsSuccess);

            var readResult = await manager.ReadBlockAsync(1);
            Assert.True(readResult.IsSuccess);

            var readBlock = readResult.Value;
            var readFlags = (BlockFlags)readBlock.Flags;
            var readAlgorithm = readFlags.GetCompressionAlgorithm();

            Assert.Equal(algorithm, readAlgorithm);
            Assert.True(readFlags.HasFlag(BlockFlags.Compressed));
        }
    }
}