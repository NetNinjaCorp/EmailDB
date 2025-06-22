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
    public class CompressionIntegrationBasicTest : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDirectory;

        public CompressionIntegrationBasicTest(ITestOutputHelper output)
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
        public async Task RawBlockManager_Should_Handle_All_Compression_Algorithms(CompressionAlgorithm algorithm)
        {
            // Arrange
            var testFile = Path.Combine(_tempDirectory, $"test_{algorithm}.edb");
            var testData = Encoding.UTF8.GetBytes("This is test data that should compress well. " +
                                                 "Compression algorithms work by finding patterns. " +
                                                 "The more patterns, the better the compression. " +
                                                 "This text has repeated words and phrases to test compression effectiveness.");

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

            // Verify compression flag is correctly set
            var flags = (BlockFlags)readBlock.Flags;
            var readAlgorithm = flags.GetCompressionAlgorithm();
            Assert.Equal(algorithm, readAlgorithm);

            // Check file size
            var fileSize = new FileInfo(testFile).Length;
            _output.WriteLine($"{algorithm}: Block size on disk = {fileSize} bytes, Original payload = {testData.Length} bytes");
            
            if (algorithm != CompressionAlgorithm.None)
            {
                _output.WriteLine($"{algorithm}: Compression integration successful");
            }
        }

        [Fact]
        public async Task Compressed_Block_Should_Be_Smaller_Than_Uncompressed()
        {
            // Create highly compressible data
            var repetitiveText = string.Join("\n", 
                System.Linq.Enumerable.Repeat("This line repeats many times to create highly compressible data for testing compression effectiveness.", 50));
            var repetitiveData = Encoding.UTF8.GetBytes(repetitiveText);

            var uncompressedFile = Path.Combine(_tempDirectory, "uncompressed.edb");
            var compressedFile = Path.Combine(_tempDirectory, "compressed_gzip.edb");

            // Create identical blocks with different compression settings
            var baseBlock = new Block
            {
                Version = 1,
                Type = BlockType.EmailBatch,
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = repetitiveData
            };

            // Write uncompressed block
            var uncompressedBlock = baseBlock;
            uncompressedBlock.BlockId = 1;
            uncompressedBlock.Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(CompressionAlgorithm.None);
            
            using (var manager = new RawBlockManager(uncompressedFile))
            {
                var result = await manager.WriteBlockAsync(uncompressedBlock);
                Assert.True(result.IsSuccess, $"Uncompressed write failed: {result.Error}");
            }

            // Write compressed block
            var compressedBlock = baseBlock;
            compressedBlock.BlockId = 2;
            compressedBlock.Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(CompressionAlgorithm.Gzip);
            
            using (var manager = new RawBlockManager(compressedFile))
            {
                var result = await manager.WriteBlockAsync(compressedBlock);
                Assert.True(result.IsSuccess, $"Compressed write failed: {result.Error}");
            }

            // Compare file sizes
            var uncompressedSize = new FileInfo(uncompressedFile).Length;
            var compressedSize = new FileInfo(compressedFile).Length;
            var compressionRatio = (double)compressedSize / uncompressedSize;

            _output.WriteLine($"Uncompressed file: {uncompressedSize} bytes");
            _output.WriteLine($"Compressed file: {compressedSize} bytes");
            _output.WriteLine($"Compression ratio: {compressionRatio:F3}");

            // For highly repetitive data, we should see significant compression
            Assert.True(compressionRatio < 0.8, "Gzip should achieve better than 80% compression on repetitive data");

            // Verify both blocks can be read correctly
            using (var uncompressedManager = new RawBlockManager(uncompressedFile))
            using (var compressedManager = new RawBlockManager(compressedFile))
            {
                var uncompressedRead = await uncompressedManager.ReadBlockAsync(1);
                var compressedRead = await compressedManager.ReadBlockAsync(2);
                
                Assert.True(uncompressedRead.IsSuccess);
                Assert.True(compressedRead.IsSuccess);
                
                Assert.Equal(uncompressedRead.Value.Payload, compressedRead.Value.Payload);
            }
        }

        [Fact]
        public async Task Empty_Payload_Should_Work_With_Compression()
        {
            var testFile = Path.Combine(_tempDirectory, "empty_compressed.edb");
            
            var block = new Block
            {
                Version = 1,
                Type = BlockType.Metadata,
                Flags = (byte)((BlockFlags)0).SetCompressionAlgorithm(CompressionAlgorithm.Gzip),
                Encoding = PayloadEncoding.Json,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                BlockId = 1,
                Payload = Array.Empty<byte>()
            };

            // Write and read back
            using (var manager = new RawBlockManager(testFile))
            {
                var writeResult = await manager.WriteBlockAsync(block);
                Assert.True(writeResult.IsSuccess, $"Write failed: {writeResult.Error}");

                var readResult = await manager.ReadBlockAsync(block.BlockId);
                Assert.True(readResult.IsSuccess, $"Read failed: {readResult.Error}");

                var readBlock = readResult.Value;
                Assert.Empty(readBlock.Payload);
                Assert.Equal(block.Flags, readBlock.Flags);
                
                var flags = (BlockFlags)readBlock.Flags;
                Assert.Equal(CompressionAlgorithm.Gzip, flags.GetCompressionAlgorithm());
            }
        }
    }
}