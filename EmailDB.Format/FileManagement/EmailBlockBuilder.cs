using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MimeKit;
using EmailDB.Format.Models.EmailContent;

namespace EmailDB.Format.FileManagement;

public class EmailEntry
{
    public MimeMessage Message { get; set; }
    public byte[] Data { get; set; }
    public int LocalId { get; set; }
    public byte[] EnvelopeHash { get; set; }
    public byte[] ContentHash { get; set; }
}

public class EmailBlockBuilder
{
    private readonly int _targetSize;
    private readonly List<EmailEntry> _pendingEmails = new();
    private int _currentSize = 0;
    
    public bool ShouldFlush => _currentSize >= _targetSize;
    public int CurrentSize => _currentSize;
    public int EmailCount => _pendingEmails.Count;
    public int TargetSize => _targetSize;
    
    public EmailBlockBuilder(int targetSizeMB)
    {
        _targetSize = targetSizeMB * 1024 * 1024;
    }
    
    public EmailEntry AddEmail(MimeMessage message, byte[] emailData)
    {
        var entry = new EmailEntry
        {
            Message = message,
            Data = emailData,
            LocalId = _pendingEmails.Count,
            EnvelopeHash = EmailBatchHashedID.ComputeEnvelopeHash(message),
            ContentHash = EmailBatchHashedID.ComputeContentHash(emailData)
        };
        
        _pendingEmails.Add(entry);
        _currentSize += emailData.Length;
        
        return entry;
    }
    
    public byte[] SerializeBlock()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Write email count
        writer.Write(_pendingEmails.Count);
        
        // Write table of contents
        var offsets = new List<long>();
        foreach (var email in _pendingEmails)
        {
            offsets.Add(ms.Position);
            writer.Write(email.Data.Length);
            writer.Write(email.EnvelopeHash);
            writer.Write(email.ContentHash);
        }
        
        // Write email data
        foreach (var email in _pendingEmails)
        {
            writer.Write(email.Data);
        }
        
        return ms.ToArray();
    }
    
    public List<EmailEntry> GetPendingEmails() => _pendingEmails.ToList();
    
    public void Clear()
    {
        _pendingEmails.Clear();
        _currentSize = 0;
    }
}