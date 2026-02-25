using EmailDB.Format;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: BTreeIndex accepts optional IBlockEncryptionProvider.
/// </summary>
public class BTreeIndexAcceptsEncryptionProviderTests : IDisposable
{
    private readonly string _tempDir;

    public BTreeIndexAcceptsEncryptionProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        BlockIdGenerator.Instance.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Constructor_WithoutEncryptionProvider_Succeeds()
    {
        // Arrange & Act — omit the encryption provider entirely
        var filePath = Path.Combine(_tempDir, "no_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Assert — EncryptionProvider is null when not supplied
        Assert.Null(btreeIndex.EncryptionProvider);
    }

    [Fact]
    public void Constructor_WithNullEncryptionProvider_Succeeds()
    {
        // Arrange & Act — explicitly pass null
        var filePath = Path.Combine(_tempDir, "null_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var btreeIndex = new BTreeIndex(rawBlockManager, encryptionProvider: null);

        // Assert
        Assert.Null(btreeIndex.EncryptionProvider);
    }

    [Fact]
    public void Constructor_WithNullProvider_StoresProvider()
    {
        // Arrange & Act — pass the NullBlockEncryptionProvider (no-op provider)
        var filePath = Path.Combine(_tempDir, "noop_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var provider = NullBlockEncryptionProvider.Instance;
        var btreeIndex = new BTreeIndex(rawBlockManager, encryptionProvider: provider);

        // Assert — provider is stored and accessible
        Assert.NotNull(btreeIndex.EncryptionProvider);
        Assert.Same(provider, btreeIndex.EncryptionProvider);
        Assert.False(btreeIndex.EncryptionProvider.IsEnabled);
    }

    [Fact]
    public void Constructor_WithKeyWrappingProvider_StoresProvider()
    {
        // Arrange — create a real KeyWrappingEncryptionProvider
        var filePath = Path.Combine(_tempDir, "real_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);

        var dek = new byte[32];
        Random.Shared.NextBytes(dek);
        var keyStore = new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new KeyStoreEntry { Epoch = 0, DEK = dek }
            }
        };
        var policy = EncryptionPolicy.Full;
        using var provider = new KeyWrappingEncryptionProvider(keyStore, policy);

        // Act
        var btreeIndex = new BTreeIndex(rawBlockManager, encryptionProvider: provider);

        // Assert — provider is stored and accessible
        Assert.NotNull(btreeIndex.EncryptionProvider);
        Assert.Same(provider, btreeIndex.EncryptionProvider);
        Assert.True(btreeIndex.EncryptionProvider.IsEnabled);
    }

    [Fact]
    public void Constructor_EncryptionProviderIsOptional_DoesNotBreakExistingUsage()
    {
        // Verify that the existing constructor pattern still works
        var filePath = Path.Combine(_tempDir, "compat.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);

        // Act — use the same call pattern as before the change
        var btreeIndex = new BTreeIndex(rawBlockManager);

        // Assert — all existing functionality still works
        Assert.Null(btreeIndex.CurrentRoot);
        Assert.Null(btreeIndex.EncryptionProvider);
    }

    [Fact]
    public void Constructor_WithExistingRootAndEncryptionProvider_Succeeds()
    {
        // Verify that all optional parameters work together
        var filePath = Path.Combine(_tempDir, "all_params.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var provider = NullBlockEncryptionProvider.Instance;

        // Act — pass existingRoot and encryption provider together
        var btreeIndex = new BTreeIndex(
            rawBlockManager,
            existingRoot: null,
            existingRootBlockOffset: -1,
            encryptionProvider: provider);

        // Assert
        Assert.Same(provider, btreeIndex.EncryptionProvider);
        Assert.Null(btreeIndex.CurrentRoot);
    }
}
