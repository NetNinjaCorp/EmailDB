using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// Derives the 32-byte Key-Encryption Key (KEK) from a password for the v3 encryption
/// bootstrap (docs/Encryption.md Section 2, spec Section 9). This is step 2 of the open
/// sequence: <c>Password → NFC normalize → Argon2id(stored params, stored salt) → KEK</c>.
///
/// <para><b>Two invariants this type exists to enforce:</b></para>
/// <list type="number">
///   <item><b>Canonicalization.</b> The password is Unicode NFC-normalized before UTF-8
///   encoding. Without this, the same password typed on two platforms can encode to
///   different bytes (e.g. precomposed vs decomposed accents) and derive different KEKs —
///   a silent cross-platform lockout (docs/Encryption.md Section 7).</item>
///   <item><b>No compiled-in KDF constants.</b> Every Argon2id cost comes from the caller's
///   <see cref="Argon2idParams"/> (unpacked from the superblock) and the caller's salt.
///   This type never reads <see cref="Argon2idParams.Default"/> during derivation, so a file
///   that stored non-default parameters still opens correctly after the defaults change.</item>
/// </list>
///
/// <para><b>Zeroization.</b> The UTF-8 password bytes derived here are scrubbed from memory
/// before returning (spec Section 9 / docs/Encryption.md Section 7). The returned KEK is the
/// caller's to zeroize — <see cref="EpochDekProvider"/> takes ownership and does so on dispose.
/// The intermediate NFC-normalized <see cref="string"/> is immutable and cannot be scrubbed;
/// that residual is a documented, unavoidable limitation of the .NET string type.</para>
/// </summary>
public static class PasswordKeyDerivation
{
    /// <summary>KEK length in bytes (AES-256 wrapping key for the KeyStore).</summary>
    public const int KekSize = 32;

    /// <summary>Salt length in bytes (the superblock <c>Salt</c> field).</summary>
    public const int SaltSize = 16;

    /// <summary>
    /// Derives the KEK from a password using Argon2id parameters and salt taken entirely from
    /// the file. Applies NFC normalization and UTF-8 encoding, then runs Argon2id with exactly
    /// the supplied costs.
    /// </summary>
    /// <param name="password">The user password; NFC-normalized then UTF-8 encoded internally.</param>
    /// <param name="parameters">Argon2id costs unpacked from the superblock <c>KdfParams</c>.</param>
    /// <param name="salt">The superblock <c>Salt</c> (exactly <see cref="SaltSize"/> bytes).</param>
    /// <returns>A fresh <see cref="KekSize"/>-byte KEK. The caller owns and must zeroize it.</returns>
    /// <exception cref="ArgumentException">The password is empty or the salt is the wrong length.</exception>
    public static byte[] DeriveKek(string password, Argon2idParams parameters, ReadOnlySpan<byte> salt)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));
        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be exactly {SaltSize} bytes, got {salt.Length}.", nameof(salt));

        // 1. NFC canonicalization before encoding — identical passwords must produce identical
        //    bytes regardless of the platform's default composition form.
        var canonical = password.Normalize(NormalizationForm.FormC);

        // 2. UTF-8 encode. This buffer is key-adjacent material and is zeroized in finally.
        var passwordBytes = Encoding.UTF8.GetBytes(canonical);
        // Konscious copies the salt into its own state, so a defensive copy keeps the
        // caller's span free of aliasing without extending its lifetime.
        var saltCopy = salt.ToArray();
        try
        {
            // 3. Argon2id driven entirely by the caller-supplied parameters — no constants.
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = saltCopy,
                MemorySize = checked((int)parameters.MemoryKB),
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return argon2.GetBytes(KekSize);
        }
        finally
        {
            // 4. Scrub the password bytes; the salt is not secret but we own the copy.
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(saltCopy);
        }
    }

    /// <summary>
    /// Convenience bootstrap overload that reads the KDF configuration straight from the raw
    /// superblock fields, validating that the file declares Argon2id before deriving.
    /// </summary>
    /// <param name="password">The user password.</param>
    /// <param name="kdfType">The superblock <c>KdfType</c> byte; must be <see cref="KdfType.Argon2id"/>.</param>
    /// <param name="kdfParams">The superblock <c>KdfParams</c> field (16 bytes).</param>
    /// <param name="salt">The superblock <c>Salt</c> field (16 bytes).</param>
    /// <exception cref="NotSupportedException">The file declares a KDF this build cannot perform.</exception>
    public static byte[] DeriveKek(
        string password, byte kdfType, ReadOnlySpan<byte> kdfParams, ReadOnlySpan<byte> salt)
    {
        if (kdfType != (byte)KdfType.Argon2id)
            throw new NotSupportedException(
                $"Unsupported KdfType {kdfType}; this build only derives keys with Argon2id (KdfType {(byte)KdfType.Argon2id}).");

        var parameters = Argon2idParams.Unpack(kdfParams);
        return DeriveKek(password, parameters, salt);
    }
}
