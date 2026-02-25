using EmailDB.Format.Encryption;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that GenerateSalt produces 16 cryptographically random bytes.
/// </summary>
public class GenerateSaltProduces16RandomBytesTests
{
    [Fact]
    public void GenerateSalt_ReturnsExactly16Bytes()
    {
        var salt = KeyDerivation.GenerateSalt();

        Assert.Equal(16, salt.Length);
    }

    [Fact]
    public void GenerateSalt_MatchesSaltSizeConstant()
    {
        var salt = KeyDerivation.GenerateSalt();

        Assert.Equal(KeyDerivation.SaltSize, salt.Length);
    }

    [Fact]
    public void GenerateSalt_ProducesNonZeroBytes()
    {
        // With 16 random bytes, the probability of all zeros is 2^-128 — effectively impossible
        var salt = KeyDerivation.GenerateSalt();

        Assert.False(salt.All(b => b == 0), "Salt should not be all zeros");
    }

    [Fact]
    public void GenerateSalt_MultipleCallsProduceDifferentSalts()
    {
        var salt1 = KeyDerivation.GenerateSalt();
        var salt2 = KeyDerivation.GenerateSalt();

        Assert.NotEqual(salt1, salt2);
    }

    [Fact]
    public void GenerateSalt_TenCallsAllUnique()
    {
        const int count = 10;
        var salts = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            salts[i] = KeyDerivation.GenerateSalt();
        }

        // Every pair must be distinct
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(16, salts[i].Length);
            for (int j = i + 1; j < count; j++)
            {
                Assert.NotEqual(salts[i], salts[j]);
            }
        }
    }

    [Fact]
    public void GenerateSalt_IsUsableForKeyDerivation()
    {
        // The generated salt should work correctly with DeriveFromPassword
        var salt = KeyDerivation.GenerateSalt();
        var key = KeyDerivation.DeriveFromPassword("test-password", salt);

        Assert.Equal(KeyDerivation.KeySize, key.Length);
    }
}
