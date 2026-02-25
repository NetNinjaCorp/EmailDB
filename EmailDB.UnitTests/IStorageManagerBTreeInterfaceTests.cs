using System.Reflection;
using EmailDB.Format;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: iStorageManager interface updated for B+-tree operations.
/// Tests that IStorageManager exposes async methods aligned with the B+-tree index
/// (AddEmailAsync, GetEmailAsync, DeleteEmailAsync, ContainsEmailAsync, etc.)
/// and that the old synchronous email methods have been replaced.
/// </summary>
public class IStorageManagerBTreeInterfaceTests
{
    private static readonly Type InterfaceType = typeof(IStorageManager);

    // --- Async B+-tree email methods exist ---

    [Fact]
    public void Interface_HasAddEmailAsync_ReturningResultOfEmailHashedID()
    {
        var method = InterfaceType.GetMethod("AddEmailAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result<EmailHashedID>>).IsAssignableFrom(method!.ReturnType),
            $"AddEmailAsync should return Task<Result<EmailHashedID>>, but returns {method.ReturnType}");

        var parameters = method.GetParameters();
        Assert.True(parameters.Length >= 2, "AddEmailAsync should have at least (byte[] emailContent, string folderName)");
        Assert.Equal(typeof(byte[]), parameters[0].ParameterType);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
    }

    [Fact]
    public void Interface_HasGetEmailAsync_ReturningResultOfByteArray()
    {
        var method = InterfaceType.GetMethod("GetEmailAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result<byte[]>>).IsAssignableFrom(method!.ReturnType),
            $"GetEmailAsync should return Task<Result<byte[]>>, but returns {method.ReturnType}");

        var parameters = method.GetParameters();
        Assert.True(parameters.Length >= 1, "GetEmailAsync should have at least (EmailHashedID emailId)");
        Assert.Equal(typeof(EmailHashedID), parameters[0].ParameterType);
    }

    [Fact]
    public void Interface_HasDeleteEmailAsync_ReturningResult()
    {
        var method = InterfaceType.GetMethod("DeleteEmailAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result>).IsAssignableFrom(method!.ReturnType),
            $"DeleteEmailAsync should return Task<Result>, but returns {method.ReturnType}");

        var parameters = method.GetParameters();
        Assert.True(parameters.Length >= 1, "DeleteEmailAsync should have at least (EmailHashedID emailId)");
        Assert.Equal(typeof(EmailHashedID), parameters[0].ParameterType);
    }

    [Fact]
    public void Interface_HasContainsEmailAsync()
    {
        var method = InterfaceType.GetMethod("ContainsEmailAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result<bool>>).IsAssignableFrom(method!.ReturnType),
            $"ContainsEmailAsync should return Task<Result<bool>>, but returns {method.ReturnType}");

        var parameters = method.GetParameters();
        Assert.True(parameters.Length >= 1, "ContainsEmailAsync should have at least (EmailHashedID emailId)");
        Assert.Equal(typeof(EmailHashedID), parameters[0].ParameterType);
    }

    [Fact]
    public void Interface_HasGetEmailCountAsync()
    {
        var method = InterfaceType.GetMethod("GetEmailCountAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result<long>>).IsAssignableFrom(method!.ReturnType),
            $"GetEmailCountAsync should return Task<Result<long>>, but returns {method.ReturnType}");
    }

    [Fact]
    public void Interface_HasRangeQueryAsync()
    {
        var method = InterfaceType.GetMethod("RangeQueryAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result<List<LeafEntry>>>).IsAssignableFrom(method!.ReturnType),
            $"RangeQueryAsync should return Task<Result<List<LeafEntry>>>, but returns {method.ReturnType}");

        var parameters = method.GetParameters();
        Assert.True(parameters.Length >= 2, "RangeQueryAsync should have at least (EmailHashedID startKey, EmailHashedID endKey)");
        Assert.Equal(typeof(EmailHashedID), parameters[0].ParameterType);
        Assert.Equal(typeof(EmailHashedID), parameters[1].ParameterType);
    }

    [Fact]
    public void Interface_HasFlushAsync()
    {
        var method = InterfaceType.GetMethod("FlushAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task).IsAssignableFrom(method!.ReturnType),
            $"FlushAsync should return Task, but returns {method.ReturnType}");
    }

    [Fact]
    public void Interface_HasVerifyIntegrityAsync()
    {
        var method = InterfaceType.GetMethod("VerifyIntegrityAsync");
        Assert.NotNull(method);
        Assert.True(typeof(Task<Result>).IsAssignableFrom(method!.ReturnType),
            $"VerifyIntegrityAsync should return Task<Result>, but returns {method.ReturnType}");
    }

    // --- CancellationToken support on all async methods ---

    [Theory]
    [InlineData("AddEmailAsync")]
    [InlineData("GetEmailAsync")]
    [InlineData("DeleteEmailAsync")]
    [InlineData("ContainsEmailAsync")]
    [InlineData("GetEmailCountAsync")]
    [InlineData("RangeQueryAsync")]
    [InlineData("FlushAsync")]
    [InlineData("VerifyIntegrityAsync")]
    public void AsyncMethods_AcceptCancellationToken(string methodName)
    {
        var method = InterfaceType.GetMethod(methodName);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        var hasCt = parameters.Any(p => p.ParameterType == typeof(CancellationToken));
        Assert.True(hasCt, $"{methodName} should accept a CancellationToken parameter");
    }

    // --- Old synchronous email methods are removed ---

    [Theory]
    [InlineData("AddEmailToFolder")]
    [InlineData("MoveEmail")]
    [InlineData("DeleteEmail")]
    [InlineData("UpdateEmailContent")]
    public void OldSynchronousEmailMethods_AreRemoved(string methodName)
    {
        var method = InterfaceType.GetMethod(methodName);
        Assert.Null(method);
    }

    // --- Non-email methods are still present ---

    [Fact]
    public void Interface_StillHasCreateFolder()
    {
        var method = InterfaceType.GetMethod("CreateFolder");
        Assert.NotNull(method);
    }

    [Fact]
    public void Interface_StillHasDeleteFolder()
    {
        var method = InterfaceType.GetMethod("DeleteFolder");
        Assert.NotNull(method);
    }

    [Fact]
    public void Interface_StillHasCompact()
    {
        var method = InterfaceType.GetMethod("Compact");
        Assert.NotNull(method);
    }

    [Fact]
    public void Interface_StillHasInvalidateCache()
    {
        var method = InterfaceType.GetMethod("InvalidateCache");
        Assert.NotNull(method);
    }

    [Fact]
    public void Interface_ExtendsIDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(InterfaceType),
            "IStorageManager should extend IDisposable");
    }
}
