using EmailDB.Format.Encryption;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies that LoadFromKeyFile reads exactly 32 bytes from a key file.
/// </summary>
public class LoadFromKeyFileReads32BytesTests
{
    [Fact]
    public void LoadFromKeyFile_WithExactly32Bytes_ReturnsKey()
    {
        var expected = new byte[32];
        Random.Shared.NextBytes(expected);

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, expected);

            var key = KeyDerivation.LoadFromKeyFile(path);

            Assert.Equal(32, key.Length);
            Assert.Equal(expected, key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromKeyFile_WithTooFewBytes_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[16]);

            var ex = Assert.Throws<ArgumentException>(() => KeyDerivation.LoadFromKeyFile(path));
            Assert.Contains("32", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromKeyFile_WithTooManyBytes_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[64]);

            var ex = Assert.Throws<ArgumentException>(() => KeyDerivation.LoadFromKeyFile(path));
            Assert.Contains("32", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromKeyFile_WithEmptyFile_Throws()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());

            Assert.Throws<ArgumentException>(() => KeyDerivation.LoadFromKeyFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromKeyFile_PreservesExactBytes()
    {
        // Write a known pattern and verify exact byte-for-byte match
        var expected = new byte[32];
        for (int i = 0; i < 32; i++)
            expected[i] = (byte)i;

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, expected);

            var key = KeyDerivation.LoadFromKeyFile(path);

            for (int i = 0; i < 32; i++)
            {
                Assert.Equal(expected[i], key[i]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
