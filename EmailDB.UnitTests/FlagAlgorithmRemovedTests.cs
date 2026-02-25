using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class FlagAlgorithmRemovedTests
{
    [Fact]
    public void Block_DoesNotHave_FlagAlgorithm_Constant()
    {
        var field = typeof(Block).GetField("FlagAlgorithm");
        Assert.Null(field);
    }

    [Fact]
    public void Block_StillHas_FlagEncrypted_Constant()
    {
        var field = typeof(Block).GetField("FlagEncrypted");
        Assert.NotNull(field);
        Assert.Equal((byte)0x01, (byte)field.GetValue(null)!);
    }
}
