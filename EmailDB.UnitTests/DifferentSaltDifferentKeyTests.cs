using System.Security.Cryptography;
using EmailDB.Format.Encryption;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that different salts produce different derived keys for the same password.
/// </summary>
public class DifferentSaltDifferentKeyTests
{
    [Fact]
    public void SamePassword_DifferentSalts_ProducesDifferentKeys()
    {
        var password = "test-password-for-salt-variation";

        var salt1 = new byte[16];
        var salt2 = new byte[16];
        RandomNumberGenerator.Fill(salt1);
        RandomNumberGenerator.Fill(salt2);

        var key1 = KeyDerivation.DeriveFromPassword(password, salt1);
        var key2 = KeyDerivation.DeriveFromPassword(password, salt2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void SamePassword_FixedDifferentSalts_ProducesDifferentKeys()
    {
        var password = "deterministic-salt-test";

        var salt1 = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var salt2 = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };

        var key1 = KeyDerivation.DeriveFromPassword(password, salt1);
        var key2 = KeyDerivation.DeriveFromPassword(password, salt2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void SamePassword_MultipleSalts_AllProduceDifferentKeys()
    {
        var password = "multi-salt-uniqueness-test";
        const int count = 5;

        var keys = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var salt = new byte[16];
            RandomNumberGenerator.Fill(salt);
            keys[i] = KeyDerivation.DeriveFromPassword(password, salt);
        }

        // Every pair of keys must be distinct
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                Assert.NotEqual(keys[i], keys[j]);
            }
        }
    }

    [Fact]
    public void SamePassword_SameSalt_ProducesSameKey_ButDifferentSaltDoesNot()
    {
        var password = "combined-check";
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        // Same salt => same key
        var key1 = KeyDerivation.DeriveFromPassword(password, salt);
        var key2 = KeyDerivation.DeriveFromPassword(password, salt);
        Assert.Equal(key1, key2);

        // Different salt => different key
        var differentSalt = new byte[16];
        RandomNumberGenerator.Fill(differentSalt);
        var key3 = KeyDerivation.DeriveFromPassword(password, differentSalt);
        Assert.NotEqual(key1, key3);
    }
}
