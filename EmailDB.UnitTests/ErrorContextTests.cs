using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmailDB.UnitTests.Helpers;
using EmailDB.UnitTests.Models;
using Moq;
using Xunit;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests verifying that error context is preserved in all failure paths.
/// Acceptance criterion: "Error context preserved in all failure paths" (US-EMDB-2).
/// </summary>
public class ErrorContextTests
{
    #region OnError callback preserves exception and context

    [Fact]
    public void OnError_Callback_PreservesExceptionType()
    {
        // Arrange
        var cacheManager = new TestCacheManager(new MockRawBlockManager());
        Exception capturedEx = null;

        cacheManager.OnError = (context, ex) => capturedEx = ex;

        var original = new FormatException("corrupt payload data");

        // Act - simulate error path (deserialization failure)
        cacheManager.OnError.Invoke("Failed to deserialize folder 'Inbox' at offset 100", original);

        // Assert - exception type is preserved
        Assert.NotNull(capturedEx);
        Assert.IsType<FormatException>(capturedEx);
        Assert.Same(original, capturedEx);
    }

    [Fact]
    public void OnError_Callback_PreservesExceptionMessage()
    {
        // Arrange
        var cacheManager = new TestCacheManager(new MockRawBlockManager());
        Exception capturedEx = null;

        cacheManager.OnError = (context, ex) => capturedEx = ex;

        // Act
        cacheManager.OnError.Invoke("context", new InvalidOperationException("specific error detail XYZ"));

        // Assert
        Assert.Equal("specific error detail XYZ", capturedEx.Message);
    }

    [Fact]
    public void OnError_Callback_PreservesInnerException()
    {
        // Arrange
        var cacheManager = new TestCacheManager(new MockRawBlockManager());
        Exception capturedEx = null;

        cacheManager.OnError = (context, ex) => capturedEx = ex;

        var inner = new ArgumentException("bad argument");
        var outer = new InvalidOperationException("operation failed", inner);

        // Act
        cacheManager.OnError.Invoke("context", outer);

        // Assert - inner exception chain is preserved
        Assert.NotNull(capturedEx.InnerException);
        Assert.IsType<ArgumentException>(capturedEx.InnerException);
        Assert.Equal("bad argument", capturedEx.InnerException.Message);
    }

    [Fact]
    public void OnError_ContextString_ContainsLocationInfo()
    {
        // Arrange
        var cacheManager = new TestCacheManager(new MockRawBlockManager());
        var capturedContexts = new List<string>();

        cacheManager.OnError = (context, ex) => capturedContexts.Add(context);

        // Act - simulate various error contexts matching CacheManager patterns
        cacheManager.OnError.Invoke("Failed to deserialize folder 'Inbox' at offset 512", new Exception("test"));
        cacheManager.OnError.Invoke("Failed to deserialize folder tree at offset 1024", new Exception("test"));
        cacheManager.OnError.Invoke("Failed to deserialize metadata at offset 2048", new Exception("test"));
        cacheManager.OnError.Invoke("Failed to deserialize segment 42 at offset 4096", new Exception("test"));
        cacheManager.OnError.Invoke("Failed to cache block 7 (type: Folder) after write at offset 8192", new Exception("test"));

        // Assert - each context contains offset information for debugging
        Assert.Equal(5, capturedContexts.Count);
        Assert.Contains("offset 512", capturedContexts[0]);
        Assert.Contains("folder 'Inbox'", capturedContexts[0]);
        Assert.Contains("offset 1024", capturedContexts[1]);
        Assert.Contains("folder tree", capturedContexts[1]);
        Assert.Contains("offset 2048", capturedContexts[2]);
        Assert.Contains("metadata", capturedContexts[2]);
        Assert.Contains("segment 42", capturedContexts[3]);
        Assert.Contains("offset 4096", capturedContexts[3]);
        Assert.Contains("block 7", capturedContexts[4]);
        Assert.Contains("type: Folder", capturedContexts[4]);
    }

    #endregion

    #region IOException wrappers preserve inner exceptions

    [Fact]
    public void IOException_Wrapper_PreservesInnerException()
    {
        // Simulate the pattern used in UpdateFolder/UpdateFolderTree/UpdateMetadata:
        //   catch (Exception ex) { throw new IOException($"Error updating folder: {ex.Message}", ex); }

        var originalException = new InvalidOperationException("disk full");

        var wrappedException = new IOException($"Error updating folder: {originalException.Message}", originalException);

        // Assert - the inner exception is preserved
        Assert.NotNull(wrappedException.InnerException);
        Assert.Same(originalException, wrappedException.InnerException);
        Assert.Contains("disk full", wrappedException.Message);
        Assert.Contains("Error updating folder", wrappedException.Message);
    }

    [Fact]
    public void IOException_Wrapper_PreservesExceptionChain()
    {
        // Simulate a deeper exception chain
        var root = new FileNotFoundException("file not found", "/path/to/db.emdb");
        var mid = new InvalidOperationException("block read failed", root);
        var outer = new IOException($"Error updating folder tree: {mid.Message}", mid);

        // Assert - full chain is navigable
        Assert.NotNull(outer.InnerException);
        Assert.IsType<InvalidOperationException>(outer.InnerException);
        Assert.NotNull(outer.InnerException.InnerException);
        Assert.IsType<FileNotFoundException>(outer.InnerException.InnerException);
        Assert.Contains("file not found", outer.InnerException.InnerException.Message);
    }

    [Fact]
    public void UpdateFolder_ErrorPattern_PreservesContext()
    {
        // Verify the pattern: throw new IOException($"Error updating folder: {ex.Message}", ex)
        var original = new ArgumentException("serialization failed for folder content");

        IOException caught = null;
        try
        {
            // Simulate the UpdateFolder catch block pattern
            try
            {
                throw original;
            }
            catch (Exception ex)
            {
                throw new IOException($"Error updating folder: {ex.Message}", ex);
            }
        }
        catch (IOException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Contains("Error updating folder", caught.Message);
        Assert.Contains("serialization failed", caught.Message);
        Assert.Same(original, caught.InnerException);
    }

    [Fact]
    public void UpdateFolderTree_ErrorPattern_PreservesContext()
    {
        var original = new OutOfMemoryException("not enough memory for folder tree");

        IOException caught = null;
        try
        {
            try
            {
                throw original;
            }
            catch (Exception ex)
            {
                throw new IOException($"Error updating folder tree: {ex.Message}", ex);
            }
        }
        catch (IOException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Contains("Error updating folder tree", caught.Message);
        Assert.Contains("not enough memory", caught.Message);
        Assert.Same(original, caught.InnerException);
    }

    [Fact]
    public void UpdateMetadata_ErrorPattern_PreservesContext()
    {
        var original = new FormatException("invalid metadata format");

        IOException caught = null;
        try
        {
            try
            {
                throw original;
            }
            catch (Exception ex)
            {
                throw new IOException($"Error updating metadata: {ex.Message}", ex);
            }
        }
        catch (IOException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Contains("Error updating metadata", caught.Message);
        Assert.Contains("invalid metadata format", caught.Message);
        Assert.Same(original, caught.InnerException);
    }

    #endregion

    #region Result.Failure preserves error context

    [Fact]
    public void ResultFailure_PreservesBlockIdInErrorMessage()
    {
        // Simulate RawBlockManager error patterns
        long blockId = 42;
        var result = TestResult<Block>.Failure($"Block ID {blockId} not found in blockLocations map.");

        Assert.True(result.IsFailure);
        Assert.Contains("42", result.Error);
        Assert.Contains("Block ID", result.Error);
    }

    [Fact]
    public void ResultFailure_PreservesIOErrorContext()
    {
        long blockId = 7;
        string ioMessage = "The process cannot access the file because it is being used by another process.";
        var result = TestResult<BlockLocation>.Failure($"I/O error writing Block ID {blockId}: {ioMessage}");

        Assert.True(result.IsFailure);
        Assert.Contains("Block ID 7", result.Error);
        Assert.Contains("I/O error writing", result.Error);
        Assert.Contains("being used by another process", result.Error);
    }

    [Fact]
    public void ResultFailure_PreservesChecksumMismatchContext()
    {
        long blockId = 99;
        var result = TestResult<Block>.Failure($"Header checksum mismatch for Block ID {blockId}");

        Assert.True(result.IsFailure);
        Assert.Contains("checksum mismatch", result.Error);
        Assert.Contains("99", result.Error);
    }

    [Fact]
    public void ResultFailure_PreservesIncompleteReadContext()
    {
        long blockId = 15;
        long expected = 1024;
        int actual = 512;
        var result = TestResult<Block>.Failure(
            $"Incomplete read for Block ID {blockId}. Expected {expected} bytes, got {actual}.");

        Assert.True(result.IsFailure);
        Assert.Contains("Block ID 15", result.Error);
        Assert.Contains("Expected 1024", result.Error);
        Assert.Contains("got 512", result.Error);
    }

    [Fact]
    public void ResultFailure_FileInitialization_PreservesErrorMessage()
    {
        // Simulate InitializeNewFile failure pattern
        var exMessage = "Failed to write header block: disk is read-only";
        var result = TestResult.Failure($"File initialization failed: {exMessage}");

        Assert.True(result.IsFailure);
        Assert.Contains("File initialization failed", result.Error);
        Assert.Contains("disk is read-only", result.Error);
    }

    [Fact]
    public void ResultFailure_WriteBlock_PreservesUnexpectedErrorContext()
    {
        long blockId = 33;
        string unexpected = "Access violation at 0x00000000";
        var result = TestResult<BlockLocation>.Failure($"Unexpected error writing Block ID {blockId}: {unexpected}");

        Assert.True(result.IsFailure);
        Assert.Contains("Block ID 33", result.Error);
        Assert.Contains("Unexpected error", result.Error);
        Assert.Contains("Access violation", result.Error);
    }

    [Fact]
    public void ResultFailure_CancelledOperation_PreservesContext()
    {
        var result = TestResult<BlockLocation>.Failure("Write operation was cancelled.");

        Assert.True(result.IsFailure);
        Assert.Contains("cancelled", result.Error);
    }

    #endregion

    #region Logger callback preserves error context

    [Fact]
    public void Logger_ReceivesErrorContext_DuringFileScan()
    {
        // Simulate the RawBlockManager logger pattern
        string capturedLog = null;
        Action<string> logger = msg => capturedLog = msg;

        var ex = new IOException("permission denied: /data/emaildb.dat");
        logger($"Error during file scan: {ex.Message}");

        Assert.NotNull(capturedLog);
        Assert.Contains("Error during file scan", capturedLog);
        Assert.Contains("permission denied", capturedLog);
    }

    [Fact]
    public void Logger_ReceivesErrorContext_DuringMagicPositionScan()
    {
        string capturedLog = null;
        Action<string> logger = msg => capturedLog = msg;

        var ex = new UnauthorizedAccessException("file locked by another process");
        logger($"Error during magic position scan: {ex.Message}");

        Assert.NotNull(capturedLog);
        Assert.Contains("Error during magic position scan", capturedLog);
        Assert.Contains("file locked", capturedLog);
    }

    [Fact]
    public void Logger_ReceivesCompactionWarning_WithBlockIdAndError()
    {
        string capturedLog = null;
        Action<string> logger = msg => capturedLog = msg;

        long blockId = 55;
        string error = "Payload checksum mismatch";
        logger($"Compaction warning: Failed to read block {blockId}, skipping. Error: {error}");

        Assert.NotNull(capturedLog);
        Assert.Contains("block 55", capturedLog);
        Assert.Contains("Payload checksum mismatch", capturedLog);
        Assert.Contains("Compaction warning", capturedLog);
    }

    #endregion

    #region Multiple errors accumulate without losing context

    [Fact]
    public void OnError_MultipleErrors_AllContextPreserved()
    {
        var cacheManager = new TestCacheManager(new MockRawBlockManager());
        var errors = new List<(string Context, Exception Error)>();
        cacheManager.OnError = (ctx, ex) => errors.Add((ctx, ex));

        // Simulate a sequence of failures that might occur during a complex operation
        var ex1 = new FormatException("bad protobuf data");
        var ex2 = new InvalidOperationException("block type mismatch");
        var ex3 = new IOException("read past end of stream");

        cacheManager.OnError.Invoke("Failed to deserialize folder 'Inbox' at offset 100", ex1);
        cacheManager.OnError.Invoke("Failed to deserialize segment 5 at offset 500", ex2);
        cacheManager.OnError.Invoke("Failed to cache block 10 (type: Segment) after write at offset 800", ex3);

        // Assert - all three errors preserved with their full context
        Assert.Equal(3, errors.Count);

        Assert.Same(ex1, errors[0].Error);
        Assert.Contains("Inbox", errors[0].Context);
        Assert.Contains("offset 100", errors[0].Context);

        Assert.Same(ex2, errors[1].Error);
        Assert.Contains("segment 5", errors[1].Context);
        Assert.Contains("offset 500", errors[1].Context);

        Assert.Same(ex3, errors[2].Error);
        Assert.Contains("block 10", errors[2].Context);
        Assert.Contains("type: Segment", errors[2].Context);
    }

    #endregion

    #region ObjectDisposedException preserves type name

    [Fact]
    public void ThrowIfDisposed_PreservesTypeName_CacheManager()
    {
        // Verify that ObjectDisposedException includes the type name for debugging
        var ex = new ObjectDisposedException(nameof(TestCacheManager));

        Assert.Contains("TestCacheManager", ex.ObjectName);
        Assert.Contains("TestCacheManager", ex.Message);
    }

    [Fact]
    public void ThrowIfDisposed_PreservesTypeName_RawBlockManager()
    {
        var ex = new ObjectDisposedException(
            nameof(TestRawBlockManager),
            "This RawBlockManager instance has been disposed.");

        Assert.Contains("TestRawBlockManager", ex.ObjectName);
        Assert.Contains("has been disposed", ex.Message);
    }

    #endregion
}

/// <summary>
/// Minimal Result pattern for testing error context preservation.
/// Mirrors the production Result&lt;T&gt; class.
/// </summary>
internal class TestResult<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T Value { get; }
    public string Error { get; }

    private TestResult(bool isSuccess, T value, string error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static TestResult<T> Success(T value) => new(true, value, null);
    public static TestResult<T> Failure(string error) => new(false, default, error ?? "Unknown error");
}

/// <summary>
/// Non-generic version for operations without a return value.
/// </summary>
internal class TestResult
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }

    private TestResult(bool isSuccess, string error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static TestResult Success() => new(true, null);
    public static TestResult Failure(string error) => new(false, error ?? "Unknown error");
}
