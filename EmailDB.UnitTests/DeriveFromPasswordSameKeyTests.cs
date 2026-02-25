using System.Security.Cryptography;
using EmailDB.Format.Encryption;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that same password + salt always produces the same 32-byte key.
/// </summary>
public class DeriveFromPasswordSameKeyTests
{
    [Fact]
    public void SamePasswordAndSalt_ProducesSameKey()
    {
        var password = "deterministic-test-password";
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var key1 = KeyDerivation.DeriveFromPassword(password, salt);
        var key2 = KeyDerivation.DeriveFromPassword(password, salt);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void SamePasswordAndSalt_ProducesSameKey_MultipleInvocations()
    {
        var password = "repeated-derivation-test";
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var keys = new byte[5][];
        for (int i = 0; i < 5; i++)
        {
            keys[i] = KeyDerivation.DeriveFromPassword(password, salt);
        }

        for (int i = 1; i < keys.Length; i++)
        {
            Assert.Equal(keys[0], keys[i]);
        }
    }

    [Fact]
    public void SamePasswordAndSalt_ProducesExactly32Bytes()
    {
        var password = "length-check-password";
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var key = KeyDerivation.DeriveFromPassword(password, salt);

        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void SamePasswordAndSalt_WithFixedSalt_ProducesKnownStableOutput()
    {
        // Use a fixed salt so the derived key is fully deterministic across runs
        var password = "stable-output-test";
        var salt = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        var key1 = KeyDerivation.DeriveFromPassword(password, salt);
        var key2 = KeyDerivation.DeriveFromPassword(password, salt);

        Assert.Equal(32, key1.Length);
        Assert.Equal(key1, key2);
    }
}
