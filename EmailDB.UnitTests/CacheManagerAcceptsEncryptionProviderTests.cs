using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using EmailDB.Format.Protobuf;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: CacheManager accepts optional IBlockEncryptionProvider.
/// </summary>
public class CacheManagerAcceptsEncryptionProviderTests : IDisposable
{
    private readonly string _tempDir;

    public CacheManagerAcceptsEncryptionProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer);

        // Assert — EncryptionProvider is null when not supplied
        Assert.Null(cacheManager.EncryptionProvider);
    }

    [Fact]
    public void Constructor_WithNullEncryptionProvider_Succeeds()
    {
        // Arrange & Act — explicitly pass null
        var filePath = Path.Combine(_tempDir, "null_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: null);

        // Assert
        Assert.Null(cacheManager.EncryptionProvider);
    }

    [Fact]
    public void Constructor_WithNullProvider_StoresProvider()
    {
        // Arrange & Act — pass the NullBlockEncryptionProvider (no-op provider)
        var filePath = Path.Combine(_tempDir, "noop_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        var provider = NullBlockEncryptionProvider.Instance;
        using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        // Assert — provider is stored and accessible
        Assert.NotNull(cacheManager.EncryptionProvider);
        Assert.Same(provider, cacheManager.EncryptionProvider);
        Assert.False(cacheManager.EncryptionProvider.IsEnabled);
    }

    [Fact]
    public void Constructor_WithEncryptionProvider_StoresProvider()
    {
        // Arrange — create a real KeyWrappingEncryptionProvider
        var filePath = Path.Combine(_tempDir, "real_provider.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

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
        using var cacheManager = new CacheManager(rawBlockManager, serializer, encryptionProvider: provider);

        // Assert — provider is stored and accessible
        Assert.NotNull(cacheManager.EncryptionProvider);
        Assert.Same(provider, cacheManager.EncryptionProvider);
        Assert.True(cacheManager.EncryptionProvider.IsEnabled);
    }

    [Fact]
    public void Constructor_EncryptionProviderIsOptional_DoesNotBreakExistingUsage()
    {
        // Verify that the existing 2-argument constructor pattern still works
        // (important for backward compatibility — no existing callers should break)
        var filePath = Path.Combine(_tempDir, "compat.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();

        // Act — use the same call pattern as before the change
        using var cacheManager = new CacheManager(rawBlockManager, serializer);

        // Assert — all existing functionality still works
        Assert.NotNull(cacheManager.Serializer);
        Assert.Null(cacheManager.EncryptionProvider);
    }

    [Fact]
    public void Constructor_WithMaxCacheSizeAndEncryptionProvider_Succeeds()
    {
        // Verify that the optional parameters after encryptionProvider still work
        var filePath = Path.Combine(_tempDir, "all_params.emdb");
        using var rawBlockManager = new RawBlockManager(filePath);
        var serializer = new ProtobufBlockContentSerializer();
        var provider = NullBlockEncryptionProvider.Instance;

        // Act — pass all optional parameters
        using var cacheManager = new CacheManager(
            rawBlockManager,
            serializer,
            encryptionProvider: provider,
            maxCacheSize: 500,
            cacheTimeout: TimeSpan.FromMinutes(10));

        // Assert
        Assert.Same(provider, cacheManager.EncryptionProvider);
    }

}
