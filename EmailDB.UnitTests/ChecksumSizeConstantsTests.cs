using EmailDB.Format.FileManagement;

namespace EmailDB.UnitTests;

public class ChecksumSizeConstantsTests
{
    [Fact]
    public void HeaderChecksumSize_Is16()
    {
        Assert.Equal(16, RawBlockManager.HeaderChecksumSize);
    }

    [Fact]
    public void PayloadChecksumSize_Is16()
    {
        Assert.Equal(16, RawBlockManager.PayloadChecksumSize);
    }

    [Fact]
    public void TotalFixedOverhead_Is84()
    {
        Assert.Equal(84, RawBlockManager.TotalFixedOverhead);
    }

    [Fact]
    public void TotalFixedOverhead_EqualsComponentSum()
    {
        int expected = RawBlockManager.HeaderSize
                     + RawBlockManager.HeaderChecksumSize
                     + RawBlockManager.PayloadChecksumSize
                     + RawBlockManager.FooterSize;
        Assert.Equal(RawBlockManager.TotalFixedOverhead, expected);
    }
}
