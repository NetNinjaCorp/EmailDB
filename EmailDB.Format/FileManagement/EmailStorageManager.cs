using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Tenray.ZoneTree;
using EmailDB.Format.Models;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Manages email storage with deduplication and batching.
/// </summary>
public class EmailStorageManager
{
    private readonly RawBlockManager _blockManager;
    private readonly AdaptiveBlockSizer _sizer;
    private readonly IZoneTree<string, string> _envelopeHashIndex;
    private readonly IZoneTree<string, string> _contentHashIndex;
    private readonly IZoneTree<string, string> _messageIdIndex;
    private EmailBlockBuilder _currentBuilder;
    private long _databaseSize;
    
    public EmailStorageManager(
        RawBlockManager blockManager,
        IZoneTree<string, string> envelopeHashIndex,
        IZoneTree<string, string> contentHashIndex,
        IZoneTree<string, string> messageIdIndex)
    {
        _blockManager = blockManager;
        _sizer = new AdaptiveBlockSizer();
        _envelopeHashIndex = envelopeHashIndex;
        _contentHashIndex = contentHashIndex;
        _messageIdIndex = messageIdIndex;
    }
    
    /// <summary>
    /// Stores an email with deduplication checking.
    /// </summary>
    public async Task<Result<EmailBatchHashedID>> StoreEmailAsync(
        MimeMessage message, 
        byte[] emailData)
    {
        // Check for duplicates
        var envelopeHash = EmailBatchHashedID.ComputeEnvelopeHash(message);
        var existingId = await CheckDuplicateAsync(envelopeHash);
        if (existingId != null)
            return Result<EmailBatchHashedID>.Success(existingId);
        
        // Get appropriate block size
        var targetSizeMB = _sizer.GetTargetBlockSizeMB(_databaseSize);
        
        // Initialize builder if needed
        if (_currentBuilder == null || _currentBuilder.TargetSize != targetSizeMB * 1024 * 1024)
        {
            if (_currentBuilder?.EmailCount > 0)
                await FlushCurrentBlockAsync();
                
            _currentBuilder = new EmailBlockBuilder(targetSizeMB);
        }
        
        // Add email to builder
        var entry = _currentBuilder.AddEmail(message, emailData);
        
        // Create pending ID
        var pendingId = new EmailBatchHashedID
        {
            LocalId = entry.LocalId,
            EnvelopeHash = entry.EnvelopeHash,
            ContentHash = entry.ContentHash
            // BlockId will be set on flush
        };
        
        // Flush if needed
        if (_currentBuilder.ShouldFlush)
        {
            var flushResult = await FlushCurrentBlockAsync();
            if (flushResult.IsSuccess)
            {
                pendingId.BlockId = flushResult.Value;
            }
        }
        
        return Result<EmailBatchHashedID>.Success(pendingId);
    }
    
    private async Task<EmailBatchHashedID> CheckDuplicateAsync(byte[] envelopeHash)
    {
        var hashKey = Convert.ToBase64String(envelopeHash);
        
        if (_envelopeHashIndex.TryGet(hashKey, out var compoundKey))
        {
            return EmailBatchHashedID.FromCompoundKey(compoundKey);
        }
        
        return null;
    }
    
    private async Task<Result<long>> FlushCurrentBlockAsync()
    {
        if (_currentBuilder == null || _currentBuilder.EmailCount == 0)
            return Result<long>.Failure("No emails to flush");
        
        // Serialize block
        var blockData = _currentBuilder.SerializeBlock();
        
        // Write block with compression
        var block = new Block
        {
            Type = BlockType.EmailBatch,
            Payload = blockData,
            PayloadLength = blockData.Length,
            Encoding = PayloadEncoding.RawBytes,
            Flags = (byte)BlockFlags.None.SetCompressionAlgorithm(CompressionAlgorithm.LZ4)
        };
        
        var blockIdResult = await _blockManager.WriteBlockAsync(block);
        if (!blockIdResult.IsSuccess)
            return Result<long>.Failure(blockIdResult.Error);
            
        var blockId = block.BlockId;
        
        // Update indexes for all emails in block
        foreach (var email in _currentBuilder.GetPendingEmails())
        {
            var compoundKey = $"{blockId}:{email.LocalId}";
            
            // Update all indexes
            _envelopeHashIndex.Upsert(
                Convert.ToBase64String(email.EnvelopeHash), 
                compoundKey);
            _contentHashIndex.Upsert(
                Convert.ToBase64String(email.ContentHash), 
                compoundKey);
            _messageIdIndex.Upsert(
                email.Message.MessageId, 
                compoundKey);
        }
        
        // Update database size
        _databaseSize += _currentBuilder.CurrentSize;
        
        // Clear builder
        _currentBuilder.Clear();
        
        return Result<long>.Success(blockId);
    }
    
    /// <summary>
    /// Flushes any pending emails to storage.
    /// </summary>
    public async Task<Result> FlushPendingEmailsAsync()
    {
        if (_currentBuilder?.EmailCount > 0)
        {
            var result = await FlushCurrentBlockAsync();
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        return Result.Success();
    }
}