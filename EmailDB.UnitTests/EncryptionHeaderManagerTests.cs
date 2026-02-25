using System.Text;
using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-42:
/// "EncryptionHeaderManager reads/writes header"
/// </summary>
public class EncryptionHeaderManagerTests
{
    private const string TestPassword = "test-password-42";

    private static EncryptionHeader BuildFullHeader(string password)
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(password, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var plainBytes = Encoding.ASCII.GetBytes("EMDB");
        var token = provider.Encrypt(plainBytes, BlockType.Metadata, blockId: 0);

        return new EncryptionHeader
        {
            Magic = (byte[])EncryptionHeader.MagicBytes.Clone(),
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = salt,
            KeyVerificationToken = token
        };
    }

    [Fact]
    public void WriteHeader_ThenReadHeader_RoundTrips()
    {
        var original = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, original);

        stream.Position = 0;
        var result = EncryptionHeaderManager.ReadHeader(stream);

        Assert.True(result.IsSuccess);
        var read = result.Value;

        Assert.Equal(original.Magic, read.Magic);
        Assert.Equal(original.SchemeVersion, read.SchemeVersion);
        Assert.Equal(original.AlgorithmId, read.AlgorithmId);
        Assert.Equal(original.KdfType, read.KdfType);
        Assert.Equal(original.Salt, read.Salt);
        Assert.Equal(original.KeyVerificationToken, read.KeyVerificationToken);
    }

    [Fact]
    public void WriteHeader_MagicBytes_AreFirstFourBytes()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        var bytes = stream.ToArray();
        Assert.Equal((byte)'E', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'B', bytes[3]);
    }

    [Fact]
    public void WriteHeader_SchemeVersion_AtOffset4()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        var bytes = stream.ToArray();
        Assert.Equal(EncryptionHeader.CurrentSchemeVersion, bytes[4]);
    }

    [Fact]
    public void WriteHeader_AlgorithmId_AtOffset5()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        var bytes = stream.ToArray();
        Assert.Equal(EncryptionHeader.AlgorithmAes256Gcm, bytes[5]);
    }

    [Fact]
    public void WriteHeader_KdfType_AtOffset6()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        var bytes = stream.ToArray();
        Assert.Equal(EncryptionHeader.KdfArgon2Id, bytes[6]);
    }

    [Fact]
    public void WriteHeader_Salt_AtOffset7_Is16Bytes()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        var bytes = stream.ToArray();
        var saltFromStream = new byte[16];
        Array.Copy(bytes, 7, saltFromStream, 0, 16);
        Assert.Equal(header.Salt, saltFromStream);
    }

    [Fact]
    public void WriteHeader_TokenLengthPrefix_AtOffset23()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        var bytes = stream.ToArray();
        int tokenLength = BitConverter.ToInt32(bytes, 23);
        Assert.Equal(header.KeyVerificationToken.Length, tokenLength);
    }

    [Fact]
    public void WriteHeader_TotalSize_MatchesFixedPlusToken()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        int expectedSize = EncryptionHeaderManager.FixedSize + header.KeyVerificationToken.Length;
        Assert.Equal(expectedSize, (int)stream.Length);
    }

    [Fact]
    public void ReadHeader_FromEmptyStream_ReturnsFailure()
    {
        using var stream = new MemoryStream();
        var result = EncryptionHeaderManager.ReadHeader(stream);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ReadHeader_FromTruncatedStream_ReturnsFailure()
    {
        // Write only magic bytes — incomplete header
        using var stream = new MemoryStream(new byte[] { 0x45, 0x4D, 0x44, 0x42 });
        var result = EncryptionHeaderManager.ReadHeader(stream);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void RoundTrip_KeyVerificationToken_StillDecrypts()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword(TestPassword, salt);
        using var provider = new AesGcmBlockEncryptionProvider(key);

        var token = provider.Encrypt(
            Encoding.ASCII.GetBytes("EMDB"), BlockType.Metadata, blockId: 0);

        var header = new EncryptionHeader
        {
            Salt = salt,
            KeyVerificationToken = token
        };

        // Write and read back
        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);
        stream.Position = 0;
        var result = EncryptionHeaderManager.ReadHeader(stream);
        Assert.True(result.IsSuccess);

        // Re-derive key from round-tripped salt and verify token still decrypts
        var reDerivedKey = KeyDerivation.DeriveFromPassword(TestPassword, result.Value.Salt);
        using var verifier = new AesGcmBlockEncryptionProvider(reDerivedKey);
        var decrypted = verifier.Decrypt(result.Value.KeyVerificationToken, BlockType.Metadata, blockId: 0);
        Assert.Equal("EMDB", Encoding.ASCII.GetString(decrypted));
    }

    [Fact]
    public void HasEncryptionHeader_WithEncryptedFile_ReturnsTrue()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);

        Assert.True(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void HasEncryptionHeader_WithEmptyStream_ReturnsFalse()
    {
        using var stream = new MemoryStream();
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void HasEncryptionHeader_WithNonEncryptedData_ReturnsFalse()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF });
        Assert.False(EncryptionHeaderManager.HasEncryptionHeader(stream));
    }

    [Fact]
    public void HasEncryptionHeader_DoesNotAdvanceStreamPosition()
    {
        var header = BuildFullHeader(TestPassword);

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);
        stream.Position = 5; // arbitrary non-zero position

        EncryptionHeaderManager.HasEncryptionHeader(stream);

        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public void WriteHeader_NullStream_Throws()
    {
        var header = new EncryptionHeader();
        Assert.Throws<ArgumentNullException>(() =>
            EncryptionHeaderManager.WriteHeader(null!, header));
    }

    [Fact]
    public void WriteHeader_NullHeader_Throws()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() =>
            EncryptionHeaderManager.WriteHeader(stream, null!));
    }

    [Fact]
    public void ReadHeader_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EncryptionHeaderManager.ReadHeader(null!));
    }

    [Fact]
    public void RoundTrip_MultipleHeadersSequentially_EachReadsCorrectly()
    {
        var header1 = BuildFullHeader("password-one");
        var header2 = BuildFullHeader("password-two");

        using var stream = new MemoryStream();

        // Write two headers back-to-back
        EncryptionHeaderManager.WriteHeader(stream, header1);
        long secondHeaderStart = stream.Position;
        EncryptionHeaderManager.WriteHeader(stream, header2);

        // Read first header
        stream.Position = 0;
        var result1 = EncryptionHeaderManager.ReadHeader(stream);
        Assert.True(result1.IsSuccess);
        Assert.Equal(header1.Salt, result1.Value.Salt);
        Assert.Equal(header1.KeyVerificationToken, result1.Value.KeyVerificationToken);

        // Read second header
        stream.Position = secondHeaderStart;
        var result2 = EncryptionHeaderManager.ReadHeader(stream);
        Assert.True(result2.IsSuccess);
        Assert.Equal(header2.Salt, result2.Value.Salt);
        Assert.Equal(header2.KeyVerificationToken, result2.Value.KeyVerificationToken);
    }

    [Fact]
    public void RoundTrip_EmptyToken_Works()
    {
        var header = new EncryptionHeader
        {
            Salt = KeyDerivation.GenerateSalt(),
            KeyVerificationToken = Array.Empty<byte>()
        };

        using var stream = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(stream, header);
        stream.Position = 0;

        var result = EncryptionHeaderManager.ReadHeader(stream);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.KeyVerificationToken);
    }
}
