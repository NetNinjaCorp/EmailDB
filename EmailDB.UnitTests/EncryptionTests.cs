using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using EmailDB.Format.Encryption;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the encryption system including providers and key management.
/// </summary>
[Trait("Category", "Encryption")]
public class EncryptionTests : IDisposable
{
    private readonly string _testFile;
    
    public EncryptionTests()
    {
        _testFile = Path.GetTempFileName();
    }
    
    [Fact]
    public void EncryptionFactory_CreateProvider_ReturnsCorrectProvider()
    {
        var aesProvider = EncryptionFactory.CreateProvider(EncryptionAlgorithm.AES256_GCM);
        var chachaProvider = EncryptionFactory.CreateProvider(EncryptionAlgorithm.ChaCha20_Poly1305);
        var cbcProvider = EncryptionFactory.CreateProvider(EncryptionAlgorithm.AES256_CBC_HMAC);
        var noneProvider = EncryptionFactory.CreateProvider(EncryptionAlgorithm.None);
        
        Assert.Equal(EncryptionAlgorithm.AES256_GCM, aesProvider.Algorithm);
        Assert.Equal(EncryptionAlgorithm.ChaCha20_Poly1305, chachaProvider.Algorithm);
        Assert.Equal(EncryptionAlgorithm.AES256_CBC_HMAC, cbcProvider.Algorithm);
        Assert.Equal(EncryptionAlgorithm.None, noneProvider.Algorithm);
    }
    
    [Theory]
    [InlineData(EncryptionAlgorithm.AES256_GCM, 32)]
    [InlineData(EncryptionAlgorithm.ChaCha20_Poly1305, 32)]
    [InlineData(EncryptionAlgorithm.AES256_CBC_HMAC, 64)]
    [InlineData(EncryptionAlgorithm.None, 0)]
    public void EncryptionProvider_KeySizes_AreCorrect(EncryptionAlgorithm algorithm, int expectedKeySize)
    {
        var provider = EncryptionFactory.CreateProvider(algorithm);
        Assert.Equal(expectedKeySize, provider.KeySizeBytes);
    }
    
    [Fact]
    public async Task Aes256GcmProvider_EncryptDecrypt_RoundTrip()
    {
        var provider = new Aes256GcmEncryptionProvider();
        var key = provider.GenerateKey();
        var testData = System.Text.Encoding.UTF8.GetBytes("Hello, encrypted world!");
        var blockId = 12345L;
        
        // Encrypt
        var encryptResult = await provider.EncryptAsync(testData, key, blockId);
        Assert.True(encryptResult.IsSuccess);
        Assert.NotEqual(testData, encryptResult.Value);
        
        // Decrypt
        var decryptResult = await provider.DecryptAsync(encryptResult.Value, key, blockId);
        Assert.True(decryptResult.IsSuccess);
        Assert.Equal(testData, decryptResult.Value);
    }
    
    [Fact]
    public async Task ChaCha20Poly1305Provider_EncryptDecrypt_RoundTrip()
    {
        var provider = new ChaCha20Poly1305EncryptionProvider();
        var key = provider.GenerateKey();
        var testData = System.Text.Encoding.UTF8.GetBytes("ChaCha20 test data!");
        var blockId = 67890L;
        
        // Encrypt
        var encryptResult = await provider.EncryptAsync(testData, key, blockId);
        Assert.True(encryptResult.IsSuccess);
        Assert.NotEqual(testData, encryptResult.Value);
        
        // Decrypt
        var decryptResult = await provider.DecryptAsync(encryptResult.Value, key, blockId);
        Assert.True(decryptResult.IsSuccess);
        Assert.Equal(testData, decryptResult.Value);
    }
    
    [Fact]
    public async Task Aes256CbcHmacProvider_EncryptDecrypt_RoundTrip()
    {
        var provider = new Aes256CbcHmacEncryptionProvider();
        var key = provider.GenerateKey();
        var testData = System.Text.Encoding.UTF8.GetBytes("AES-CBC-HMAC test data!");
        var blockId = 11111L;
        
        // Encrypt
        var encryptResult = await provider.EncryptAsync(testData, key, blockId);
        Assert.True(encryptResult.IsSuccess);
        Assert.NotEqual(testData, encryptResult.Value);
        
        // Decrypt
        var decryptResult = await provider.DecryptAsync(encryptResult.Value, key, blockId);
        Assert.True(decryptResult.IsSuccess);
        Assert.Equal(testData, decryptResult.Value);
    }
    
    [Fact]
    public async Task EncryptionProvider_WrongKey_FailsDecryption()
    {
        var provider = new Aes256GcmEncryptionProvider();
        var correctKey = provider.GenerateKey();
        var wrongKey = provider.GenerateKey();
        var testData = System.Text.Encoding.UTF8.GetBytes("This should fail with wrong key");
        var blockId = 99999L;
        
        // Encrypt with correct key
        var encryptResult = await provider.EncryptAsync(testData, correctKey, blockId);
        Assert.True(encryptResult.IsSuccess);
        
        // Try to decrypt with wrong key
        var decryptResult = await provider.DecryptAsync(encryptResult.Value, wrongKey, blockId);
        Assert.False(decryptResult.IsSuccess);
    }
    
    [Fact]
    public async Task EncryptionProvider_WrongBlockId_FailsDecryption()
    {
        var provider = new Aes256GcmEncryptionProvider();
        var key = provider.GenerateKey();
        var testData = System.Text.Encoding.UTF8.GetBytes("This should fail with wrong block ID");
        var correctBlockId = 12345L;
        var wrongBlockId = 54321L;
        
        // Encrypt with correct block ID
        var encryptResult = await provider.EncryptAsync(testData, key, correctBlockId);
        Assert.True(encryptResult.IsSuccess);
        
        // Try to decrypt with wrong block ID
        var decryptResult = await provider.DecryptAsync(encryptResult.Value, key, wrongBlockId);
        Assert.False(decryptResult.IsSuccess);
    }
    
    [Fact]
    public void KeyManager_GenerateBlockKey_CreatesCorrectKey()
    {
        var keyManager = new EncryptionKeyManager();
        var masterKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(masterKey);
        
        var keyManagerContent = new KeyManagerContent();
        var unlockResult = keyManager.Unlock(masterKey, keyManagerContent);
        Assert.True(unlockResult.IsSuccess);
        
        var blockId = 12345L;
        var algorithm = EncryptionAlgorithm.AES256_GCM;
        
        var keyResult = keyManager.GenerateBlockKey(blockId, algorithm);
        Assert.True(keyResult.IsSuccess);
        Assert.Equal(32, keyResult.Value.Length); // AES-256 key size
        
        // Should be able to retrieve the same key
        var retrieveResult = keyManager.GetBlockKey(blockId);
        Assert.True(retrieveResult.IsSuccess);
        Assert.Equal(keyResult.Value, retrieveResult.Value);
    }
    
    [Fact]
    public void KeyManager_Lock_ClearsKeys()
    {
        var keyManager = new EncryptionKeyManager();
        var masterKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(masterKey);
        
        var keyManagerContent = new KeyManagerContent();
        var unlockResult = keyManager.Unlock(masterKey, keyManagerContent);
        Assert.True(unlockResult.IsSuccess);
        Assert.True(keyManager.IsUnlocked);
        
        keyManager.Lock();
        Assert.False(keyManager.IsUnlocked);
        
        // Should not be able to get keys when locked
        var keyResult = keyManager.GetBlockKey(12345L);
        Assert.False(keyResult.IsSuccess);
    }
    
    [Fact]
    public async Task EncryptedBlockManager_WriteRead_RoundTrip()
    {
        using var blockManager = new EncryptedBlockManager(_testFile);
        
        // Set up encryption
        var masterKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(masterKey);
        var unlockResult = blockManager.UnlockEncryption(masterKey);
        Assert.True(unlockResult.IsSuccess);
        
        // Create test block
        var testBlock = new Block
        {
            Type = BlockType.EmailBatch,
            BlockId = 12345L,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = System.Text.Encoding.UTF8.GetBytes("This is encrypted test data!"),
            Encoding = PayloadEncoding.Json,
            Flags = (byte)BlockFlags.None
        };
        
        // Write encrypted block
        var writeResult = await blockManager.WriteBlockAsync(testBlock, EncryptionAlgorithm.AES256_GCM);
        Assert.True(writeResult.IsSuccess);
        
        // Read and decrypt block
        var readResult = await blockManager.ReadBlockAsync(testBlock.BlockId);
        Assert.True(readResult.IsSuccess);
        
        var readBlock = readResult.Value;
        Assert.Equal(testBlock.Type, readBlock.Type);
        Assert.Equal(testBlock.BlockId, readBlock.BlockId);
        Assert.Equal(testBlock.Payload, readBlock.Payload);
    }
    
    [Fact]
    public async Task EncryptedBlockManager_UnencryptedBlock_PassesThrough()
    {
        using var blockManager = new EncryptedBlockManager(_testFile);
        
        // Create test block (no encryption)
        var testBlock = new Block
        {
            Type = BlockType.Metadata,
            BlockId = 54321L,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Payload = System.Text.Encoding.UTF8.GetBytes("Unencrypted test data"),
            Encoding = PayloadEncoding.Json,
            Flags = (byte)BlockFlags.None
        };
        
        // Write unencrypted block
        var writeResult = await blockManager.WriteBlockAsync(testBlock, EncryptionAlgorithm.None);
        Assert.True(writeResult.IsSuccess);
        
        // Read block (should be unencrypted)
        var readResult = await blockManager.ReadBlockAsync(testBlock.BlockId);
        Assert.True(readResult.IsSuccess);
        
        var readBlock = readResult.Value;
        Assert.Equal(testBlock.Payload, readBlock.Payload);
        Assert.Equal(EncryptionAlgorithm.None, ((BlockFlags)readBlock.Flags).GetEncryptionAlgorithm());
    }
    
    public void Dispose()
    {
        try
        {
            if (File.Exists(_testFile))
            {
                File.Delete(_testFile);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}