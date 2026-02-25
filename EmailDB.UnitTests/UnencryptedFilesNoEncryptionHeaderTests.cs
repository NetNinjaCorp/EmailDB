using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-42:
/// "Unencrypted files have no EncryptionHeader"
/// </summary>
public class UnencryptedFilesNoEncryptionHeaderTests : IDisposable
{
    private readonly string _testFilePath;

    public UnencryptedFilesNoEncryptionHeaderTests()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"test_no_enc_header_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
            File.Delete(_testFilePath);
    }

    [Fact]
    public async Task UnencryptedBlockFile_HasNoEncryptionHeader()
    {
        // Write a block using RawBlockManager (no encryption)
        using (var manager = new RawBlockManager(_testFilePath))
        {
            var block = new Block
            {
                BlockId = 1,
                Type = BlockType.Metadata,
                Version = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload = Encoding.UTF8.GetBytes("test payload")
            };
            await manager.WriteBlockAsync(block);
        }

        // Verify the file does NOT start with encryption magic
        using var fileStream = new FileStream(_testFilePath, FileMode.Open, FileAccess.Read);
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(fileStream));
    }

    [Fact]
    public void EmptyFile_HasNoEncryptionHeader()
    {
        using var stream = new MemoryStream();
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void RandomData_HasNoEncryptionHeader()
    {
        var random = new byte[256];
        new Random(42).NextBytes(random);

        // Ensure first 4 bytes are NOT "EMDB" magic
        Assert.NotEqual(EncryptionHeader.MagicBytes, random[..4]);

        using var stream = new MemoryStream(random);
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void RawBlockManagerMagic_IsNotEncryptionMagic()
    {
        // RawBlockManager's HEADER_MAGIC must differ from EncryptionHeader magic
        var headerMagicBytes = BitConverter.GetBytes(RawBlockManager.HEADER_MAGIC);
        Assert.NotEqual(EncryptionHeader.MagicBytes, headerMagicBytes[..4]);
    }

    [Fact]
    public void PlainTextFile_HasNoEncryptionHeader()
    {
        var content = Encoding.UTF8.GetBytes("This is a plain text file with no encryption.");
        using var stream = new MemoryStream(content);
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void EncryptedFile_HasEncryptionHeader_PositiveControl()
    {
        // Positive control: a file WITH an encryption header should return true
        var header = new EncryptionHeader
        {
            Salt = KeyDerivation.GenerateSalt(),
            KeyVerificationToken = new byte[] { 0x01, 0x02, 0x03 }
        };

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        Assert.True(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void StreamWithPartialMagic_HasNoEncryptionHeader()
    {
        // Only 3 of the 4 magic bytes — should not be treated as encrypted
        var partialMagic = new byte[] { (byte)'E', (byte)'M', (byte)'D' };
        using var stream = new MemoryStream(partialMagic);
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public async Task MultipleUnencryptedBlocks_FileHasNoEncryptionHeader()
    {
        using (var manager = new RawBlockManager(_testFilePath))
        {
            for (int i = 1; i <= 5; i++)
            {
                var block = new Block
                {
                    BlockId = i,
                    Type = BlockType.EmailContent,
                    Version = 1,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Payload = Encoding.UTF8.GetBytes($"email content {i}")
                };
                await manager.WriteBlockAsync(block);
            }
        }

        using var fileStream = new FileStream(_testFilePath, FileMode.Open, FileAccess.Read);
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(fileStream));
    }
}
