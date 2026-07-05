using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Unit tests for the v3 superblock <see cref="KeyVerificationToken"/> (task US-EMDB-76-7,
/// docs/Encryption.md Section 2 step 3, spec Section 3.1 tamper note / Section 13).
/// The token is <c>Nonce(12) ‖ AES-256-GCM(KEK, "EMDB")(4) ‖ Tag(16)</c> = 32 bytes; it
/// lets the open sequence fast-fail on a wrong password or a tampered Salt/KdfParams with a
/// distinct <see cref="WrongKeyOrTamperError"/>, before the KeyStore is touched.
/// </summary>
public class V3KeyVerificationTokenTests
{
    private static byte[] Kek() => RandomNumberGenerator.GetBytes(KeyVerificationToken.KekSize);

    // --- Layout ---

    [Fact]
    public void Create_ProducesThirtyTwoByteToken()
    {
        var token = KeyVerificationToken.Create(Kek());
        Assert.Equal(32, token.Length);
        Assert.Equal(32, KeyVerificationToken.TokenSize);
    }

    [Fact]
    public void Create_UsesFreshRandomNonce_TwoTokensDiffer()
    {
        var kek = Kek();
        var a = KeyVerificationToken.Create(kek);
        var b = KeyVerificationToken.Create(kek);

        // Same KEK, but a fresh CSPRNG nonce each time => different nonce, ciphertext, and tag.
        Assert.NotEqual(a, b);
        Assert.NotEqual(a.AsSpan(0, KeyVerificationToken.NonceSize).ToArray(),
                        b.AsSpan(0, KeyVerificationToken.NonceSize).ToArray());
    }

    [Fact]
    public void Create_DoesNotStorePlaintextInTheClear()
    {
        var token = KeyVerificationToken.Create(Kek());
        // The 4 ciphertext bytes must not equal the literal "EMDB" — it is encrypted.
        var ciphertext = token.AsSpan(KeyVerificationToken.NonceSize, 4).ToArray();
        Assert.NotEqual(Encoding.ASCII.GetBytes("EMDB"), ciphertext);
    }

    // --- Round trip / correct KEK ---

    [Fact]
    public void Verify_WithCorrectKek_Succeeds()
    {
        var kek = Kek();
        var token = KeyVerificationToken.Create(kek);

        var ex = Record.Exception(() => KeyVerificationToken.Verify(token, kek));
        Assert.Null(ex);
        Assert.True(KeyVerificationToken.TryVerify(token, kek));
    }

    // --- Wrong password / KEK ---

    [Fact]
    public void Verify_WithWrongKek_ThrowsDistinctTokenMismatch()
    {
        var token = KeyVerificationToken.Create(Kek());
        var wrongKek = Kek();

        var ex = Assert.Throws<WrongKeyOrTamperError>(
            () => KeyVerificationToken.Verify(token, wrongKek));

        // Distinct error class from corruption, and specifically the token-mismatch cause.
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
        Assert.Equal(TamperCause.TokenMismatch, ex.Cause);
        Assert.False(KeyVerificationToken.TryVerify(token, wrongKek));
    }

    // --- End-to-end wrong PASSWORD through the real KDF (US-EMDB-76-3 acceptance criterion) ---

    [Fact]
    public void Verify_WrongPasswordThroughRealKdf_ThrowsDistinctTokenMismatch()
    {
        // (a) The realistic open sequence: KEK is derived from the user's password using the
        //     file's stored Salt/KdfParams (no simulated wrong KEK). A wrong password with the
        //     SAME salt + params derives a different KEK, so the token must fail-fast.
        const string correctPassword = "correct horse battery staple";
        const string wrongPassword = "Correct horse battery staple"; // one-char difference
        var parameters = new Argon2idParams(1024, 1, 1); // small cost keeps the test fast
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);

        var kek = PasswordKeyDerivation.DeriveKek(correctPassword, parameters, salt);
        var token = KeyVerificationToken.Create(kek);

        // Correct password re-derives the exact KEK and opens the token.
        Assert.True(KeyVerificationToken.TryVerify(
            token, PasswordKeyDerivation.DeriveKek(correctPassword, parameters, salt)));

        // Wrong password -> wrong KEK -> distinct wrong-key/tamper error.
        var wrongKek = PasswordKeyDerivation.DeriveKek(wrongPassword, parameters, salt);
        Assert.NotEqual(kek, wrongKek); // sanity: the KDF actually produced a different key

        var ex = Assert.Throws<WrongKeyOrTamperError>(
            () => KeyVerificationToken.Verify(token, wrongKek));

        // (b) Distinct from corruption (a different Kind entirely)...
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
        Assert.NotEqual(VerificationFailureKind.Corruption, ex.Kind);
        // ...and, within WrongKeyOrTamper, distinct from a generic per-block GCM tag failure:
        // the token path reports TokenMismatch, never the AesGcmBlockCipher GcmTag cause.
        Assert.Equal(TamperCause.TokenMismatch, ex.Cause);
        Assert.NotEqual(TamperCause.GcmTag, ex.Cause);
        Assert.NotEqual(WrongKeyOrTamperError.GcmTagFailure().Cause, ex.Cause);

        Assert.False(KeyVerificationToken.TryVerify(token, wrongKek));
    }

    [Fact]
    public void Verify_FailsFast_UsesOnlyThirtyTwoByteTokenAndKek_NoKeyStoreOrBlockDecryption()
    {
        // (c) "Fails fast" = the wrong-password decision needs ONLY the 32-byte token plus the
        //     candidate KEK. There is no KeyStore, no block read, and no per-block decryption in
        //     the call path — the API surface itself takes nothing else.
        const string password = "s3kret";
        var parameters = new Argon2idParams(1024, 1, 1);
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);

        var token = KeyVerificationToken.Create(
            PasswordKeyDerivation.DeriveKek(password, parameters, salt));
        Assert.Equal(32, token.Length); // exactly the superblock token, nothing more

        var wrongKek = PasswordKeyDerivation.DeriveKek("wrong", parameters, salt);

        // Verify is a pure function of (token, kek): a 32-byte span and a 32-byte KEK. The
        // rejection is produced without constructing or touching any KeyStore/block state.
        var only32Bytes = token.AsSpan(0, KeyVerificationToken.TokenSize).ToArray();
        var ex = Assert.Throws<WrongKeyOrTamperError>(
            () => KeyVerificationToken.Verify(only32Bytes, wrongKek));
        Assert.Equal(TamperCause.TokenMismatch, ex.Cause);
        // No block was involved, so no block was named and no epoch was brute-forced.
        Assert.Null(ex.Offset);
        Assert.Null(ex.BlockId);
    }

    // --- Tampered Salt / KdfParams caught via the derived KEK ---

    [Fact]
    public void Verify_TamperedSalt_FailsToken()
    {
        // Realistic bootstrap: KEK is derived from password + stored Salt/KdfParams.
        const string password = "correct horse battery staple";
        var parameters = new Argon2idParams(1024, 1, 1); // small cost keeps the test fast
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);

        var kek = PasswordKeyDerivation.DeriveKek(password, parameters, salt);
        var token = KeyVerificationToken.Create(kek);

        // Attacker flips one Salt byte; the correct password now derives a different KEK.
        var tamperedSalt = (byte[])salt.Clone();
        tamperedSalt[0] ^= 0xFF;
        var kekAfterTamper = PasswordKeyDerivation.DeriveKek(password, parameters, tamperedSalt);
        Assert.NotEqual(kek, kekAfterTamper); // sanity: the flipped salt actually changed the KEK

        var ex = Assert.Throws<WrongKeyOrTamperError>(
            () => KeyVerificationToken.Verify(token, kekAfterTamper));
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
        Assert.Equal(TamperCause.TokenMismatch, ex.Cause);
    }

    // Argon2idParams constructor is (uint memoryKB, ushort iterations, ushort parallelism).
    // The genuine file stores (1024, 1, 1); each row tampers exactly ONE field so we prove
    // every KdfParams cost feeds the KEK and any single-field change is caught by the token.
    [Theory]
    [InlineData((uint)2048, (ushort)1, (ushort)1)] // memory tampered (1024 -> 2048)
    [InlineData((uint)1024, (ushort)2, (ushort)1)] // iterations tampered (1 -> 2)
    [InlineData((uint)1024, (ushort)1, (ushort)2)] // parallelism tampered (1 -> 2)
    public void Verify_TamperedKdfParams_FailsToken(uint memoryKB, ushort iterations, ushort parallelism)
    {
        const string password = "correct horse battery staple";
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var genuineParams = new Argon2idParams(1024, 1, 1);

        var kek = PasswordKeyDerivation.DeriveKek(password, genuineParams, salt);
        var token = KeyVerificationToken.Create(kek);

        // Attacker rewrites one KdfParams field; the correct password now derives a different KEK.
        var tamperedParams = new Argon2idParams(memoryKB, iterations, parallelism);
        var kekAfterTamper = PasswordKeyDerivation.DeriveKek(password, tamperedParams, salt);
        Assert.NotEqual(kek, kekAfterTamper); // sanity: the tampered cost actually changed the KEK

        // ...and it fails with the same distinct wrong-password-or-tampered-header class.
        var ex = Assert.Throws<WrongKeyOrTamperError>(
            () => KeyVerificationToken.Verify(token, kekAfterTamper));
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
        Assert.Equal(TamperCause.TokenMismatch, ex.Cause);
    }

    // --- Tampered token bytes ---

    [Fact]
    public void Verify_TamperedTokenBytes_Fails()
    {
        var kek = Kek();
        var token = KeyVerificationToken.Create(kek);

        // Flip a ciphertext byte: even with the right KEK the GCM tag no longer verifies.
        token[KeyVerificationToken.NonceSize] ^= 0x01;

        var ex = Assert.Throws<WrongKeyOrTamperError>(() => KeyVerificationToken.Verify(token, kek));
        Assert.Equal(TamperCause.TokenMismatch, ex.Cause);
    }

    // --- Argument validation ---

    [Fact]
    public void Create_WrongKekLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeyVerificationToken.Create(new byte[31]));
    }

    [Fact]
    public void Verify_WrongTokenLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeyVerificationToken.Verify(new byte[31], Kek()));
    }

    [Fact]
    public void Verify_WrongKekLength_Throws()
    {
        var token = KeyVerificationToken.Create(Kek());
        Assert.Throws<ArgumentException>(() => KeyVerificationToken.Verify(token, new byte[16]));
    }
}
