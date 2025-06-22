using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Enhanced block manager that supports encryption for block payloads.
/// Wraps RawBlockManager and adds encryption/decryption capabilities.
/// </summary>
public class EncryptedBlockManager : IDisposable
{
    private readonly RawBlockManager _rawBlockManager;
    private readonly EncryptionKeyManager _keyManager;
    private readonly Dictionary<EncryptionAlgorithm, IEncryptionProvider> _encryptionProviders;
    private bool _disposed;
    
    public EncryptedBlockManager(string filePath, bool createIfNotExists = true, bool isReadOnly = false)
    {
        _rawBlockManager = new RawBlockManager(filePath, createIfNotExists, isReadOnly);
        _keyManager = new EncryptionKeyManager();
        
        // Initialize encryption providers
        _encryptionProviders = new Dictionary<EncryptionAlgorithm, IEncryptionProvider>();
        foreach (var algorithm in EncryptionFactory.GetSupportedAlgorithms())
        {
            _encryptionProviders[algorithm] = EncryptionFactory.CreateProvider(algorithm);
        }
    }
    
    /// <summary>
    /// Unlocks the encryption key manager with master key.
    /// </summary>
    /// <param name="masterKey">Master encryption key</param>
    /// <param name="keyManagerContent">Key manager content (if available)</param>
    /// <returns>Success if unlocked</returns>
    public Result<bool> UnlockEncryption(byte[] masterKey, KeyManagerContent? keyManagerContent = null)
    {
        if (keyManagerContent == null)
        {
            // Create new key manager if none exists
            keyManagerContent = new KeyManagerContent();
        }
        
        return _keyManager.Unlock(masterKey, keyManagerContent);
    }
    
    /// <summary>
    /// Locks the encryption key manager.
    /// </summary>
    public void LockEncryption()
    {
        _keyManager.Lock();
    }
    
    /// <summary>
    /// Writes a block with optional encryption.
    /// </summary>
    /// <param name="block">The block to write</param>
    /// <param name="encryptionAlgorithm">Encryption algorithm to use (None for no encryption)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Block location if successful</returns>
    public async Task<Result<BlockLocation>> WriteBlockAsync(
        Block block, 
        EncryptionAlgorithm encryptionAlgorithm = EncryptionAlgorithm.None,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var processedBlock = block;
            
            // Apply encryption if requested
            if (encryptionAlgorithm != EncryptionAlgorithm.None)
            {
                var encryptResult = await EncryptBlockPayloadAsync(block, encryptionAlgorithm);
                if (!encryptResult.IsSuccess)
                    return Result<BlockLocation>.Failure($"Encryption failed: {encryptResult.Error}");
                    
                processedBlock = encryptResult.Value;
            }
            
            // Write the (possibly encrypted) block
            return await _rawBlockManager.WriteBlockAsync(processedBlock, cancellationToken);
        }
        catch (Exception ex)
        {
            return Result<BlockLocation>.Failure($"Failed to write encrypted block: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Reads a block with automatic decryption if encrypted.
    /// </summary>
    /// <param name="blockId">Block ID to read</param>
    /// <returns>Decrypted block if successful</returns>
    public async Task<Result<Block>> ReadBlockAsync(long blockId)
    {
        try
        {
            // Read the raw block
            var readResult = await _rawBlockManager.ReadBlockAsync(blockId);
            if (!readResult.IsSuccess)
                return readResult;
                
            var block = readResult.Value;
            
            // Check if block is encrypted
            var blockFlags = (BlockFlags)block.Flags;
            var encryptionAlgorithm = blockFlags.GetEncryptionAlgorithm();
            if (encryptionAlgorithm == EncryptionAlgorithm.None)
            {
                // No encryption, return as-is
                return Result<Block>.Success(block);
            }
            
            // Decrypt the block payload
            var decryptResult = await DecryptBlockPayloadAsync(block);
            if (!decryptResult.IsSuccess)
                return Result<Block>.Failure($"Decryption failed: {decryptResult.Error}");
                
            return Result<Block>.Success(decryptResult.Value);
        }
        catch (Exception ex)
        {
            return Result<Block>.Failure($"Failed to read encrypted block: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Creates or updates a KeyManager block with current keys.
    /// </summary>
    /// <param name="masterKey">Master key for encrypting the KeyManager block</param>
    /// <returns>Block location of the KeyManager block</returns>
    public async Task<Result<BlockLocation>> WriteKeyManagerBlockAsync(byte[] masterKey)
    {
        try
        {
            if (!_keyManager.IsUnlocked)
                return Result<BlockLocation>.Failure("Key manager is locked");
                
            // Create KeyManager content
            var contentResult = _keyManager.ToContent();
            if (!contentResult.IsSuccess)
                return Result<BlockLocation>.Failure($"Failed to create key manager content: {contentResult.Error}");
                
            var keyManagerContent = contentResult.Value;
            
            // Serialize the content
            var serializer = new DefaultBlockContentSerializer();
            var payload = serializer.Serialize(keyManagerContent);
            
            // Create KeyManager block
            var keyManagerBlock = new Block
            {
                Type = BlockType.KeyManager,
                BlockId = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), // Use timestamp as block ID
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Payload = payload,
                Encoding = PayloadEncoding.Json,
                Flags = (byte)BlockFlags.None // Will be set by encryption
            };
            
            // Encrypt the KeyManager block itself with master key
            // First, generate a key for the KeyManager block
            var keyResult = _keyManager.GenerateBlockKey(keyManagerBlock.BlockId, EncryptionAlgorithm.AES256_GCM, BlockType.KeyManager);
            if (!keyResult.IsSuccess)
                return Result<BlockLocation>.Failure($"Failed to generate key for KeyManager block: {keyResult.Error}");
            
            // Write the encrypted KeyManager block
            return await WriteBlockAsync(keyManagerBlock, EncryptionAlgorithm.AES256_GCM);
        }
        catch (Exception ex)
        {
            return Result<BlockLocation>.Failure($"Failed to write KeyManager block: {ex.Message}");
        }
    }
    
    private async Task<Result<Block>> EncryptBlockPayloadAsync(Block block, EncryptionAlgorithm algorithm)
    {
        try
        {
            if (!_encryptionProviders.TryGetValue(algorithm, out var provider))
                return Result<Block>.Failure($"Encryption algorithm {algorithm} not supported");
                
            // Get or generate encryption key for this block
            var keyResult = _keyManager.GetBlockKey(block.BlockId);
            if (!keyResult.IsSuccess)
            {
                // Generate new key if not exists
                var genResult = _keyManager.GenerateBlockKey(block.BlockId, algorithm, block.Type);
                if (!genResult.IsSuccess)
                    return Result<Block>.Failure($"Failed to generate encryption key: {genResult.Error}");
                keyResult = Result<byte[]>.Success(genResult.Value);
            }
            
            var key = keyResult.Value;
            
            // Encrypt the payload
            var encryptResult = await provider.EncryptAsync(block.Payload, key, block.BlockId);
            if (!encryptResult.IsSuccess)
                return Result<Block>.Failure($"Encryption failed: {encryptResult.Error}");
                
            // Create new block with encrypted payload and updated flags
            var encryptedBlock = new Block
            {
                Type = block.Type,
                BlockId = block.BlockId,
                Timestamp = block.Timestamp,
                Payload = encryptResult.Value,
                Encoding = block.Encoding,
                Flags = (byte)((BlockFlags)block.Flags).SetEncryptionAlgorithm(algorithm)
            };
            
            return Result<Block>.Success(encryptedBlock);
        }
        catch (Exception ex)
        {
            return Result<Block>.Failure($"Encryption failed: {ex.Message}");
        }
    }
    
    private async Task<Result<Block>> DecryptBlockPayloadAsync(Block encryptedBlock)
    {
        try
        {
            var blockFlags = (BlockFlags)encryptedBlock.Flags;
            var algorithm = blockFlags.GetEncryptionAlgorithm();
            
            if (!_encryptionProviders.TryGetValue(algorithm, out var provider))
                return Result<Block>.Failure($"Encryption algorithm {algorithm} not supported");
                
            // Get decryption key for this block
            var keyResult = _keyManager.GetBlockKey(encryptedBlock.BlockId);
            if (!keyResult.IsSuccess)
                return Result<Block>.Failure($"Failed to get decryption key: {keyResult.Error}");
                
            var key = keyResult.Value;
            
            // Decrypt the payload
            var decryptResult = await provider.DecryptAsync(encryptedBlock.Payload, key, encryptedBlock.BlockId);
            if (!decryptResult.IsSuccess)
                return Result<Block>.Failure($"Decryption failed: {decryptResult.Error}");
                
            // Create new block with decrypted payload and cleared encryption flags
            var decryptedBlock = new Block
            {
                Type = encryptedBlock.Type,
                BlockId = encryptedBlock.BlockId,
                Timestamp = encryptedBlock.Timestamp,
                Payload = decryptResult.Value,
                Encoding = encryptedBlock.Encoding,
                Flags = (byte)((BlockFlags)encryptedBlock.Flags).SetEncryptionAlgorithm(EncryptionAlgorithm.None)
            };
            
            return Result<Block>.Success(decryptedBlock);
        }
        catch (Exception ex)
        {
            return Result<Block>.Failure($"Decryption failed: {ex.Message}");
        }
    }
    
    // Delegate other methods to RawBlockManager
    public async Task<Result<Block>> ReadBlockAsync(long blockId, CancellationToken cancellationToken = default)
        => await ReadBlockAsync(blockId);
        
    public List<BlockLocation> GetBlockLocations() => _rawBlockManager.GetBlockLocations().Values.ToList();
    public string FilePath => _rawBlockManager.FilePath;
    public bool IsEncryptionUnlocked => _keyManager.IsUnlocked;
    public int ManagedKeyCount => _keyManager.KeyCount;
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _keyManager?.Lock();
            
            _encryptionProviders.Clear();
            
            _rawBlockManager?.Dispose();
            _disposed = true;
        }
    }
}