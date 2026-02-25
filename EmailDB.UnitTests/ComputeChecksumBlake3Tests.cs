using EmailDB.Format.FileManagement;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that RawBlockManager.ComputeChecksum returns byte[16] using BLAKE3.
/// Validates acceptance criterion: "ComputeChecksum returns byte[16] using BLAKE3"
/// </summary>
public class ComputeChecksumBlake3Tests
{
    [Fact]
    public void ComputeChecksum_ReturnsExactly16Bytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var checksum = RawBlockManager.ComputeChecksum(data);

        Assert.Equal(16, checksum.Length);
    }

    [Fact]
    public void ComputeChecksum_EmptyInput_Returns16Bytes()
    {
        var checksum = RawBlockManager.ComputeChecksum(Array.Empty<byte>());

        Assert.Equal(16, checksum.Length);
    }

    [Fact]
    public void ComputeChecksum_IsDeterministic()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var checksum1 = RawBlockManager.ComputeChecksum(data);
        var checksum2 = RawBlockManager.ComputeChecksum(data);

        Assert.Equal(checksum1, checksum2);
    }

    [Fact]
    public void ComputeChecksum_DifferentInputs_ProduceDifferentResults()
    {
        var checksum1 = RawBlockManager.ComputeChecksum(new byte[] { 0x00 });
        var checksum2 = RawBlockManager.ComputeChecksum(new byte[] { 0x01 });

        Assert.NotEqual(checksum1, checksum2);
    }

    [Fact]
    public void ComputeChecksum_MatchesBlake3TruncatedTo16Bytes()
    {
        var data = new byte[] { 10, 20, 30, 40, 50 };
        var checksum = RawBlockManager.ComputeChecksum(data);

        // Compute expected value using Blake3 directly
        var fullHash = Blake3.Hasher.Hash(data).AsSpan().ToArray();
        var expected = fullHash.AsSpan(0, 16).ToArray();

        Assert.Equal(expected, checksum);
    }

    [Fact]
    public void ComputeChecksum_WithOffsetAndCount_ReturnsExactly16Bytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var checksum = RawBlockManager.ComputeChecksum(data, 2, 4);

        Assert.Equal(16, checksum.Length);
    }

    [Fact]
    public void ComputeChecksum_WithOffsetAndCount_MatchesBlake3TruncatedTo16Bytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var checksum = RawBlockManager.ComputeChecksum(data, 2, 4);

        // Compute expected: BLAKE3 of bytes {3, 4, 5, 6} truncated to 16
        var slice = new byte[] { 3, 4, 5, 6 };
        var fullHash = Blake3.Hasher.Hash(slice).AsSpan().ToArray();
        var expected = fullHash.AsSpan(0, 16).ToArray();

        Assert.Equal(expected, checksum);
    }

    [Fact]
    public void ComputeChecksum_WithOffsetAndCount_EqualsFullArrayChecksum()
    {
        // ComputeChecksum(data, 0, data.Length) should equal ComputeChecksum(data)
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var checksumFull = RawBlockManager.ComputeChecksum(data);
        var checksumSlice = RawBlockManager.ComputeChecksum(data, 0, data.Length);

        Assert.Equal(checksumFull, checksumSlice);
    }

    [Fact]
    public void ComputeChecksum_LargePayload_Returns16Bytes()
    {
        var data = new byte[4096];
        new Random(42).NextBytes(data);

        var checksum = RawBlockManager.ComputeChecksum(data);

        Assert.Equal(16, checksum.Length);
    }

    [Fact]
    public void ComputeChecksum_IsNotAllZeros_ForNonEmptyInput()
    {
        var data = new byte[] { 1, 2, 3 };
        var checksum = RawBlockManager.ComputeChecksum(data);

        Assert.NotEqual(new byte[16], checksum);
    }
}
