using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that EncryptionHeader contains: magic, scheme version, algorithm ID,
/// KDF type, salt, and key verification token.
/// Acceptance criterion for US-EMDB-42.
/// </summary>
public class EncryptionHeaderFieldsTests
{
    [Fact]
    public void Header_HasMagicProperty()
    {
        var header = new EncryptionHeader();
        Assert.NotNull(header.Magic);
    }

    [Fact]
    public void Header_MagicDefaultsToEMDB()
    {
        var header = new EncryptionHeader();
        Assert.Equal(Encoding.ASCII.GetBytes("EMDB"), header.Magic);
        Assert.Equal(EncryptionHeader.MagicBytes, header.Magic);
    }

    [Fact]
    public void Header_MagicBytesConstant_IsFourBytes()
    {
        Assert.Equal(4, EncryptionHeader.MagicBytes.Length);
        Assert.Equal((byte)'E', EncryptionHeader.MagicBytes[0]);
        Assert.Equal((byte)'M', EncryptionHeader.MagicBytes[1]);
        Assert.Equal((byte)'D', EncryptionHeader.MagicBytes[2]);
        Assert.Equal((byte)'B', EncryptionHeader.MagicBytes[3]);
    }

    [Fact]
    public void Header_HasSchemeVersionProperty()
    {
        var header = new EncryptionHeader();
        _ = header.SchemeVersion;
    }

    [Fact]
    public void Header_SchemeVersionDefaultsToCurrentVersion()
    {
        var header = new EncryptionHeader();
        Assert.Equal(EncryptionHeader.CurrentSchemeVersion, header.SchemeVersion);
        Assert.Equal(1, header.SchemeVersion);
    }

    [Fact]
    public void Header_SchemeVersion_IsSettable()
    {
        var header = new EncryptionHeader { SchemeVersion = 2 };
        Assert.Equal(2, header.SchemeVersion);
    }

    [Fact]
    public void Header_HasAlgorithmIdProperty()
    {
        var header = new EncryptionHeader();
        _ = header.AlgorithmId;
    }

    [Fact]
    public void Header_AlgorithmIdDefaultsToAes256Gcm()
    {
        var header = new EncryptionHeader();
        Assert.Equal(EncryptionHeader.AlgorithmAes256Gcm, header.AlgorithmId);
        Assert.Equal(0x01, header.AlgorithmId);
    }

    [Fact]
    public void Header_HasKdfTypeProperty()
    {
        var header = new EncryptionHeader();
        _ = header.KdfType;
    }

    [Fact]
    public void Header_KdfTypeDefaultsToArgon2Id()
    {
        var header = new EncryptionHeader();
        Assert.Equal(EncryptionHeader.KdfArgon2Id, header.KdfType);
        Assert.Equal(0x01, header.KdfType);
    }

    [Fact]
    public void Header_HasSaltProperty()
    {
        var header = new EncryptionHeader();
        Assert.NotNull(header.Salt);
    }

    [Fact]
    public void Header_Salt_IsSettableToGeneratedSalt()
    {
        var salt = KeyDerivation.GenerateSalt();
        var header = new EncryptionHeader { Salt = salt };
        Assert.Equal(salt, header.Salt);
        Assert.Equal(KeyDerivation.SaltSize, header.Salt.Length);
    }

    [Fact]
    public void Header_HasKeyVerificationTokenProperty()
    {
        var header = new EncryptionHeader();
        Assert.NotNull(header.KeyVerificationToken);
    }

    [Fact]
    public void Header_KeyVerificationToken_IsSettable()
    {
        var token = new byte[] { 0x01, 0x02, 0x03 };
        var header = new EncryptionHeader { KeyVerificationToken = token };
        Assert.Equal(token, header.KeyVerificationToken);
    }

    [Fact]
    public void Header_AllFieldsPopulated_RoundTrip()
    {
        var salt = KeyDerivation.GenerateSalt();
        var token = new byte[32];
        RandomNumberGenerator.Fill(token);

        var header = new EncryptionHeader
        {
            Magic = EncryptionHeader.MagicBytes,
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = salt,
            KeyVerificationToken = token
        };

        // Verify all six fields are present and populated
        Assert.Equal(EncryptionHeader.MagicBytes, header.Magic);
        Assert.Equal(1, header.SchemeVersion);
        Assert.Equal(0x01, header.AlgorithmId);
        Assert.Equal(0x01, header.KdfType);
        Assert.Equal(KeyDerivation.SaltSize, header.Salt.Length);
        Assert.Equal(token, header.KeyVerificationToken);
    }

    [Fact]
    public void Header_DefaultInstance_HasAllSixFields()
    {
        var header = new EncryptionHeader();

        // All six fields must exist and have defaults
        Assert.NotNull(header.Magic);
        Assert.True(header.SchemeVersion > 0);
        Assert.True(header.AlgorithmId > 0);
        Assert.True(header.KdfType > 0);
        Assert.NotNull(header.Salt);
        Assert.NotNull(header.KeyVerificationToken);
    }

    [Fact]
    public void Header_MagicBytes_AreIndependentPerInstance()
    {
        var h1 = new EncryptionHeader();
        var h2 = new EncryptionHeader();

        // Default magic values are equal
        Assert.Equal(h1.Magic, h2.Magic);

        // But modifying one does not affect the other
        h1.Magic[0] = 0xFF;
        Assert.NotEqual(h1.Magic, h2.Magic);
    }
}
