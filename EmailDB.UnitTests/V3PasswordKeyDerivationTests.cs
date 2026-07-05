using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.V3;
using Konscious.Security.Cryptography;

namespace EmailDB.UnitTests;

/// <summary>
/// Unit tests for the v3 password KDF (task US-EMDB-76-6, docs/Encryption.md Section 2):
/// NFC canonicalization before UTF-8 encoding, Argon2id driven entirely by the
/// superblock-supplied <see cref="Argon2idParams"/> and salt (no compiled-in constants),
/// and the 16-byte KdfParams pack/unpack round trip.
/// </summary>
public class V3PasswordKeyDerivationTests
{
    private static byte[] Salt() => RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);

    // --- NFC normalization ---

    [Fact]
    public void DeriveKek_NfcNormalizesPassword_ComposedAndDecomposedFormsMatch()
    {
        var salt = Salt();
        var parameters = new Argon2idParams(1024, 1, 1); // small cost keeps the test fast

        // Precomposed "\u00e9" (NFC) vs decomposed "e" + combining acute "\u0301" (NFD),
        // built from explicit code points so the file encoding cannot mask the difference.
        var composed = "pa\u00e9ss";        // U+00E9
        var decomposed = "pae\u0301ss";     // e + U+0301
        Assert.NotEqual(composed, decomposed); // distinct code-point sequences...

        var kekComposed = PasswordKeyDerivation.DeriveKek(composed, parameters, salt);
        var kekDecomposed = PasswordKeyDerivation.DeriveKek(decomposed, parameters, salt);

        // ...that must derive the SAME key after NFC normalization.
        Assert.Equal(kekComposed, kekDecomposed);
    }

    [Fact]
    public void DeriveKek_NfcNormalizesPassword_HangulComposedAndDecomposedFormsMatch()
    {
        var salt = Salt();
        var parameters = new Argon2idParams(1024, 1, 1);

        // Non-Latin, multi-codepoint case. Hangul syllable U+AC00 (NFC) vs its
        // conjoining-jamo decomposition U+1100 + U+1161 (NFD), built from explicit
        // code points so the file encoding cannot mask the difference.
        var composed = "\uAC00pw";          // U+AC00
        var decomposed = "\u1100\u1161pw";  // U+1100 + U+1161
        Assert.NotEqual(composed, decomposed);  // distinct code-point sequences...
        Assert.True(decomposed.Length > composed.Length); // ...of different lengths...

        var kekComposed = PasswordKeyDerivation.DeriveKek(composed, parameters, salt);
        var kekDecomposed = PasswordKeyDerivation.DeriveKek(decomposed, parameters, salt);

        // ...that must derive the SAME key after NFC normalization.
        Assert.Equal(kekComposed, kekDecomposed);
    }

    [Fact]
    public void DeriveKek_DifferentPasswords_ProduceDifferentKeys()
    {
        var salt = Salt();
        var parameters = new Argon2idParams(1024, 1, 1);

        // Same salt and params: only the password differs, so any difference in the KEK is
        // attributable to the password bytes (guards against NFC normalization collapsing
        // genuinely distinct passwords to the same key).
        var kekA = PasswordKeyDerivation.DeriveKek("pa\u00e9ss", parameters, salt);
        var kekB = PasswordKeyDerivation.DeriveKek("pa\u00e8ss", parameters, salt); // \u00e8, not \u00e9

        Assert.NotEqual(kekA, kekB);
    }

    [Fact]
    public void DeriveKek_UsesNfcThenUtf8_MatchesIndependentComputation()
    {
        var salt = Salt();
        var parameters = new Argon2idParams(1024, 2, 1);
        var password = "café-Åé"; // mix of precomposed accents

        var kek = PasswordKeyDerivation.DeriveKek(password, parameters, salt);

        // Independent reference: NFC -> UTF-8 -> Argon2id with the exact same costs.
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        using var argon2 = new Argon2id(bytes)
        {
            Salt = salt,
            MemorySize = 1024,
            Iterations = 2,
            DegreeOfParallelism = 1,
        };
        var expected = argon2.GetBytes(PasswordKeyDerivation.KekSize);

        Assert.Equal(expected, kek);
    }

    // --- Parameters honored, no compiled-in constants ---

    [Fact]
    public void DeriveKek_HonorsSuppliedParams_NotDefaults()
    {
        var salt = Salt();
        const string password = "correct horse battery staple";

        // Non-default params (defaults are 65536/3/4).
        var custom = new Argon2idParams(2048, 4, 2);
        var kek = PasswordKeyDerivation.DeriveKek(password, custom, salt);

        // Reference computed with the SAME non-default costs must match.
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        using var argon2 = new Argon2id(bytes)
        {
            Salt = salt,
            MemorySize = 2048,
            Iterations = 4,
            DegreeOfParallelism = 2,
        };
        Assert.Equal(argon2.GetBytes(PasswordKeyDerivation.KekSize), kek);
    }

    [Fact]
    public void DeriveKek_DifferentIterations_ProduceDifferentKeys()
    {
        var salt = Salt();
        const string password = "same-password";

        var kek3 = PasswordKeyDerivation.DeriveKek(password, new Argon2idParams(1024, 3, 1), salt);
        var kek5 = PasswordKeyDerivation.DeriveKek(password, new Argon2idParams(1024, 5, 1), salt);

        Assert.NotEqual(kek3, kek5);
    }

    [Fact]
    public void DeriveKek_Returns32ByteKek()
    {
        var kek = PasswordKeyDerivation.DeriveKek("pw", new Argon2idParams(1024, 1, 1), Salt());
        Assert.Equal(PasswordKeyDerivation.KekSize, kek.Length);
    }

    // --- Bootstrap overload reads raw superblock fields ---

    [Fact]
    public void DeriveKek_FromRawSuperblockFields_MatchesTypedOverload()
    {
        var salt = Salt();
        const string password = "bootstrap";
        var parameters = new Argon2idParams(1024, 2, 1);

        var viaTyped = PasswordKeyDerivation.DeriveKek(password, parameters, salt);
        var viaRaw = PasswordKeyDerivation.DeriveKek(
            password, (byte)KdfType.Argon2id, parameters.Pack(), salt);

        Assert.Equal(viaTyped, viaRaw);
    }

    [Fact]
    public void DeriveKek_FromRawSuperblockFields_RejectsUnsupportedKdfType()
    {
        Assert.Throws<NotSupportedException>(() =>
            PasswordKeyDerivation.DeriveKek(
                "pw", kdfType: 99, new Argon2idParams(1024, 1, 1).Pack(), Salt()));
    }

    // --- Argument validation ---

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void DeriveKek_RejectsEmptyPassword(string? password)
    {
        Assert.Throws<ArgumentException>(() =>
            PasswordKeyDerivation.DeriveKek(password!, new Argon2idParams(1024, 1, 1), Salt()));
    }

    [Fact]
    public void DeriveKek_RejectsWrongLengthSalt()
    {
        Assert.Throws<ArgumentException>(() =>
            PasswordKeyDerivation.DeriveKek("pw", new Argon2idParams(1024, 1, 1), new byte[8]));
    }

    // --- Argon2idParams packing (superblock KdfParams, spec Section 3.2) ---

    [Fact]
    public void Argon2idParams_PackUnpack_RoundTrips()
    {
        var original = new Argon2idParams(65_536, 3, 4);
        var packed = original.Pack();

        Assert.Equal(Argon2idParams.PackedSize, packed.Length);
        var restored = Argon2idParams.Unpack(packed);

        Assert.Equal(original.MemoryKB, restored.MemoryKB);
        Assert.Equal(original.Iterations, restored.Iterations);
        Assert.Equal(original.Parallelism, restored.Parallelism);
    }

    [Fact]
    public void Argon2idParams_Pack_LayoutIsLittleEndianWithZeroReserved()
    {
        var packed = new Argon2idParams(0x01020304, 0x0506, 0x0708).Pack();

        // [MemoryKB (4B LE)] [Iterations (2B LE)] [Parallelism (2B LE)] [Reserved (8B, zero)]
        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, packed[0..4]);
        Assert.Equal(new byte[] { 0x06, 0x05 }, packed[4..6]);
        Assert.Equal(new byte[] { 0x08, 0x07 }, packed[6..8]);
        Assert.All(packed[8..16], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Argon2idParams_Unpack_IgnoresReservedBytes()
    {
        var packed = new Argon2idParams(1024, 2, 1).Pack();
        // Tampering with the reserved region is not rejected here; the KeyVerificationToken
        // catches any change to the derived KEK downstream.
        for (var i = 8; i < 16; i++) packed[i] = 0xFF;

        var restored = Argon2idParams.Unpack(packed);
        Assert.Equal(1024u, restored.MemoryKB);
        Assert.Equal((ushort)2, restored.Iterations);
        Assert.Equal((ushort)1, restored.Parallelism);
    }

    [Fact]
    public void Argon2idParams_Default_Is64Mb_3_4()
    {
        var d = Argon2idParams.Default;
        Assert.Equal(65_536u, d.MemoryKB);
        Assert.Equal((ushort)3, d.Iterations);
        Assert.Equal((ushort)4, d.Parallelism);
    }

    [Fact]
    public void Argon2idParams_Constructor_RejectsInvalidCosts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Argon2idParams(1024, 0, 1)); // 0 iterations
        Assert.Throws<ArgumentOutOfRangeException>(() => new Argon2idParams(1024, 1, 0)); // 0 lanes
        Assert.Throws<ArgumentOutOfRangeException>(() => new Argon2idParams(4, 1, 2));     // memory < 8*lanes
    }

    [Fact]
    public void Argon2idParams_Unpack_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => Argon2idParams.Unpack(new byte[15]));
        Assert.Throws<ArgumentException>(() => Argon2idParams.Unpack(new byte[17]));
    }
}
