using System.Reflection;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

public class IBlockEncryptionProviderInterfaceTests
{
    private static readonly Type InterfaceType = typeof(IBlockEncryptionProvider);

    [Fact]
    public void Interface_HasEncryptMethod_WithCorrectSignature()
    {
        var method = InterfaceType.GetMethod("Encrypt");
        Assert.NotNull(method);
        Assert.Equal(typeof(byte[]), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[0].ParameterType);
        Assert.Equal(typeof(BlockType), parameters[1].ParameterType);
        Assert.Equal(typeof(long), parameters[2].ParameterType);
    }

    [Fact]
    public void Interface_HasDecryptMethod_WithCorrectSignature()
    {
        var method = InterfaceType.GetMethod("Decrypt");
        Assert.NotNull(method);
        Assert.Equal(typeof(byte[]), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(ReadOnlySpan<byte>), parameters[0].ParameterType);
        Assert.Equal(typeof(BlockType), parameters[1].ParameterType);
        Assert.Equal(typeof(long), parameters[2].ParameterType);
    }

    [Fact]
    public void Interface_HasShouldEncryptMethod_WithCorrectSignature()
    {
        var method = InterfaceType.GetMethod("ShouldEncrypt");
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(BlockType), parameters[0].ParameterType);
    }

    [Fact]
    public void Interface_HasOverheadBytesProperty_ReturningInt()
    {
        var property = InterfaceType.GetProperty("OverheadBytes");
        Assert.NotNull(property);
        Assert.Equal(typeof(int), property.PropertyType);
        Assert.True(property.CanRead);
    }

    [Fact]
    public void Interface_HasIsEnabledProperty_ReturningBool()
    {
        var property = InterfaceType.GetProperty("IsEnabled");
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.True(property.CanRead);
    }

    [Fact]
    public void Interface_ExtendsIDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(InterfaceType));
    }
}
