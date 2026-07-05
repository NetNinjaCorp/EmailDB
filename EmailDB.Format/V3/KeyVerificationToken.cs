using System.Security.Cryptography;
using System.Text;

namespace EmailDB.Format.V3;

/// <summary>
/// The superblock's 32-byte <c>KeyVerificationToken</c> (EmailDB_FileFormat_Spec.md
/// Section 3.1, docs/Encryption.md Section 2 step 3). It is the fast-fail check that
/// lets the open sequence distinguish a wrong password (or a tampered header) from every
/// other failure <em>before</em> touching the KeyStore.
///
/// <para><b>On-disk layout</b> (exactly <see cref="TokenSize"/> = 32 bytes):</para>
/// <code>Nonce (12) ‖ AES-256-GCM(KEK, "EMDB") ciphertext (4) ‖ Tag (16)</code>
///
/// <para><b>How it works.</b> The token is the constant plaintext <c>"EMDB"</c> sealed under
/// the KEK with AES-256-GCM and a fresh random nonce. <see cref="Verify"/> re-derives the
/// KEK from the candidate password (using the superblock's own Salt/KdfParams) and decrypts
/// the token. Because the KEK is derived from the Salt, KdfParams, KdfType, and AlgorithmId,
/// altering <em>any</em> of those header fields yields a different KEK, so the GCM tag fails —
/// the header's encryption parameters are thereby implicitly authenticated (spec Section 3.1
/// tamper note). A tag failure is surfaced as the distinct
/// <see cref="WrongKeyOrTamperError"/> ("wrong password OR tampered header"), never as
/// corruption and never as a generic per-block <see cref="WrongKeyOrTamperError.GcmTagFailure"/>.</para>
///
/// <para><b>Why no AAD.</b> The token deliberately uses empty AAD: the only thing it needs to
/// bind to is the KEK itself, which already transitively covers every header field that feeds
/// the KDF. There is nothing else to authenticate here (unlike per-block ciphertext, which
/// binds FileId/BlockId/BlockType/KeyEpoch via <see cref="AesGcmBlockCipher"/>).</para>
///
/// <para><b>Key material.</b> The KEK is borrowed for a single call and never copied or
/// retained here; ownership and zeroization of the KEK live with the caller
/// (<see cref="EpochDekProvider"/>).</para>
/// </summary>
public static class KeyVerificationToken
{
    /// <summary>GCM nonce length in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>GCM authentication tag length in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>KEK length in bytes (AES-256).</summary>
    public const int KekSize = 32;

    /// <summary>The constant sealed plaintext: the four ASCII bytes <c>"EMDB"</c>.</summary>
    public static ReadOnlySpan<byte> Plaintext => "EMDB"u8;

    /// <summary>Total token length: Nonce (12) + ciphertext (4) + Tag (16) = 32 bytes.</summary>
    public const int TokenSize = NonceSize + 4 + TagSize; // 32

    /// <summary>
    /// Creates a fresh 32-byte <c>KeyVerificationToken</c> for <paramref name="kek"/>: a random
    /// nonce, the ciphertext of the constant <c>"EMDB"</c> plaintext, and the GCM tag.
    /// Used when creating an encrypted file or on password change / key rotation.
    /// </summary>
    /// <param name="kek">The 32-byte KEK the token verifies against.</param>
    /// <returns>A new <see cref="TokenSize"/>-byte token for the superblock.</returns>
    /// <exception cref="ArgumentException">The KEK is not exactly 32 bytes.</exception>
    public static byte[] Create(ReadOnlySpan<byte> kek)
    {
        if (kek.Length != KekSize)
            throw new ArgumentException($"KEK must be exactly {KekSize} bytes for AES-256-GCM.", nameof(kek));

        var token = new byte[TokenSize];
        var nonce = token.AsSpan(0, NonceSize);
        var ciphertext = token.AsSpan(NonceSize, Plaintext.Length);
        var tag = token.AsSpan(NonceSize + Plaintext.Length, TagSize);

        // Fresh CSPRNG nonce on every create — never a counter or derived value.
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(nonce, Plaintext, ciphertext, tag);

        return token;
    }

    /// <summary>
    /// Verifies a candidate <paramref name="kek"/> against the stored token. On success returns
    /// silently; on a GCM tag mismatch throws the distinct
    /// <see cref="WrongKeyOrTamperError"/> — the wrong-password / tampered-header signal the open
    /// sequence fast-fails on (spec Section 13, docs/Encryption.md Section 2 step 3).
    /// </summary>
    /// <param name="token">The stored superblock token (exactly <see cref="TokenSize"/> bytes).</param>
    /// <param name="kek">The candidate KEK derived from the supplied password and stored KDF fields.</param>
    /// <exception cref="ArgumentException">The token or KEK is the wrong length.</exception>
    /// <exception cref="WrongKeyOrTamperError">
    /// The token did not decrypt under this KEK: wrong password, or a tampered
    /// Salt/KdfParams/KdfType/AlgorithmId that changed the derived KEK. This is a distinct error
    /// class from corruption and from a generic per-block tag failure.
    /// </exception>
    public static void Verify(ReadOnlySpan<byte> token, ReadOnlySpan<byte> kek)
    {
        if (token.Length != TokenSize)
            throw new ArgumentException($"KeyVerificationToken must be exactly {TokenSize} bytes, got {token.Length}.", nameof(token));
        if (kek.Length != KekSize)
            throw new ArgumentException($"KEK must be exactly {KekSize} bytes for AES-256-GCM.", nameof(kek));

        var nonce = token[..NonceSize];
        var ciphertext = token.Slice(NonceSize, 4);
        var tag = token.Slice(NonceSize + 4, TagSize);

        Span<byte> plaintext = stackalloc byte[4];
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            // Wrong KEK: wrong password, or a tampered Salt/KdfParams/KdfType/AlgorithmId that
            // changed the derived KEK. Distinct from corruption (spec Section 13).
            CryptographicOperations.ZeroMemory(plaintext);
            throw WrongKeyOrTamperError.TokenMismatch();
        }

        // Defensive: the tag alone proves authenticity, but confirm the sealed constant so a
        // future format change to Plaintext can never silently pass an old token.
        bool ok = CryptographicOperations.FixedTimeEquals(plaintext, Plaintext);
        CryptographicOperations.ZeroMemory(plaintext);
        if (!ok)
            throw WrongKeyOrTamperError.TokenMismatch();
    }

    /// <summary>
    /// Non-throwing variant of <see cref="Verify"/>: returns <c>true</c> if the KEK opens the
    /// token, <c>false</c> on a tag mismatch. Argument-length errors still throw.
    /// </summary>
    public static bool TryVerify(ReadOnlySpan<byte> token, ReadOnlySpan<byte> kek)
    {
        try
        {
            Verify(token, kek);
            return true;
        }
        catch (WrongKeyOrTamperError)
        {
            return false;
        }
    }
}
