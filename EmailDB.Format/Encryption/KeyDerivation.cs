using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace EmailDB.Format.Encryption;

public static class KeyDerivation
{
    public const int KeySize = 32;
    public const int SaltSize = 16;

    // Argon2id parameters
    internal const int Argon2MemoryKB = 65536; // 64 MB
    internal const int Argon2Iterations = 3;
    internal const int Argon2Parallelism = 4;

    public static byte[] DeriveFromPassword(string password, byte[] salt)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));
        if (salt == null || salt.Length < SaltSize)
            throw new ArgumentException($"Salt must be at least {SaltSize} bytes.", nameof(salt));

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        using var argon2 = new Argon2id(passwordBytes);
        argon2.Salt = salt;
        argon2.MemorySize = Argon2MemoryKB;
        argon2.Iterations = Argon2Iterations;
        argon2.DegreeOfParallelism = Argon2Parallelism;

        return argon2.GetBytes(KeySize);
    }

    public static byte[] LoadFromKeyFile(string path)
    {
        var keyBytes = File.ReadAllBytes(path);
        if (keyBytes.Length != KeySize)
            throw new ArgumentException($"Key file must contain exactly {KeySize} bytes, got {keyBytes.Length}.");
        return keyBytes;
    }

    public static byte[] GenerateSalt()
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }
}
