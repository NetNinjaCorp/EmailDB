using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using Konscious.Security.Cryptography;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that DeriveFromPassword uses Argon2id with the required parameters:
/// 64 MB memory, 3 iterations, 4 parallelism.
/// </summary>
public class DeriveFromPasswordArgon2idParamsTests
{
    [Fact]
    public void Constants_MemoryIs64MB()
    {
        Assert.Equal(65536, KeyDerivation.Argon2MemoryKB); // 64 * 1024 = 65536 KB
    }

    [Fact]
    public void Constants_IterationsIs3()
    {
        Assert.Equal(3, KeyDerivation.Argon2Iterations);
    }

    [Fact]
    public void Constants_ParallelismIs4()
    {
        Assert.Equal(4, KeyDerivation.Argon2Parallelism);
    }

    [Fact]
    public void DeriveFromPassword_ProducesArgon2idOutput()
    {
        // Derive a key using DeriveFromPassword
        var password = "test-password-123";
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var derivedKey = KeyDerivation.DeriveFromPassword(password, salt);

        // Independently compute the expected key using Argon2id directly
        // with the same parameters to confirm the implementation matches
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        using var argon2 = new Argon2id(passwordBytes);
        argon2.Salt = salt;
        argon2.MemorySize = 65536;  // 64 MB
        argon2.Iterations = 3;
        argon2.DegreeOfParallelism = 4;
        var expectedKey = argon2.GetBytes(32);

        Assert.Equal(expectedKey, derivedKey);
    }

    [Fact]
    public void DeriveFromPassword_ProducesDifferentOutputWithDifferentParams()
    {
        // Prove that the output depends on the specific parameters by computing
        // with a different iteration count and verifying mismatch
        var password = "test-password-123";
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var derivedKey = KeyDerivation.DeriveFromPassword(password, salt);

        // Compute with 2 iterations instead of 3
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        using var argon2 = new Argon2id(passwordBytes);
        argon2.Salt = salt;
        argon2.MemorySize = 65536;
        argon2.Iterations = 2; // different!
        argon2.DegreeOfParallelism = 4;
        var differentKey = argon2.GetBytes(32);

        Assert.NotEqual(differentKey, derivedKey);
    }

    [Fact]
    public void DeriveFromPassword_Returns32ByteKey()
    {
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword("my-password", salt);

        Assert.Equal(32, key.Length);
    }
}
