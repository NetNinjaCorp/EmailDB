using System.Reflection;
using EmailDB.Format.FileManagement;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests that Force.Crc32 is no longer used by RawBlockManager.
/// Validates acceptance criterion: "Force.Crc32 import removed from RawBlockManager"
/// </summary>
public class ForceCrc32RemovedFromRawBlockManagerTests
{
    [Fact]
    public void RawBlockManager_ComputeChecksum_ReturnsByteArray_NotUInt32()
    {
        // CRC32 returns uint; BLAKE3-128 returns byte[].
        // If Force.Crc32 were still in use, ComputeChecksum would return uint.
        var method = typeof(RawBlockManager).GetMethod(
            "ComputeChecksum",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            new[] { typeof(byte[]) });

        Assert.NotNull(method);
        Assert.Equal(typeof(byte[]), method!.ReturnType);
    }

    [Fact]
    public void RawBlockManager_NoMethodReturnsCrc32UInt()
    {
        // Verify no public/internal static method on RawBlockManager returns uint,
        // which would suggest CRC32 checksum usage.
        var methods = typeof(RawBlockManager).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        var checksumMethods = methods
            .Where(m => m.Name.Contains("Checksum", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var method in checksumMethods)
        {
            Assert.NotEqual(typeof(uint), method.ReturnType);
        }
    }

    [Fact]
    public void RawBlockManager_Assembly_DoesNotExposeForcesCrc32Types()
    {
        // Verify that RawBlockManager's public API surface has no parameters
        // or return types from the Force.Crc32 namespace.
        var methods = typeof(RawBlockManager).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        foreach (var method in methods)
        {
            Assert.DoesNotContain("Force.Crc32", method.ReturnType.FullName ?? "");

            foreach (var param in method.GetParameters())
            {
                Assert.DoesNotContain("Force.Crc32", param.ParameterType.FullName ?? "");
            }
        }
    }

    [Fact]
    public void RawBlockManager_ChecksumSize_Is16_NotCrc32Size4()
    {
        // CRC32 produces 4-byte checksums; BLAKE3-128 produces 16-byte.
        // This confirms the switch away from Force.Crc32.
        Assert.Equal(16, RawBlockManager.HeaderChecksumSize);
        Assert.Equal(16, RawBlockManager.PayloadChecksumSize);
    }

    [Fact]
    public void RawBlockManager_ComputeChecksum_ProducesBlake3Output()
    {
        // Verify output matches Blake3, confirming Force.Crc32 is not used.
        var data = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        var result = RawBlockManager.ComputeChecksum(data);

        var expected = Blake3.Hasher.Hash(data).AsSpan().Slice(0, 16).ToArray();
        Assert.Equal(expected, result);
    }
}
