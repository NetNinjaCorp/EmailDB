using System;
using System.Text;
using System.Security.Cryptography;
using System.Linq;
using MimeKit;

namespace EmailDB.Format.Models.EmailContent;

public class EmailBatchHashedID
{
    public long BlockId { get; set; }
    public int LocalId { get; set; }
    public byte[] EnvelopeHash { get; set; }
    public byte[] ContentHash { get; set; }
    
    public string ToCompoundKey() => $"{BlockId}:{LocalId}";
    
    public static EmailBatchHashedID FromCompoundKey(string key)
    {
        var parts = key.Split(':');
        return new EmailBatchHashedID 
        { 
            BlockId = long.Parse(parts[0]),
            LocalId = int.Parse(parts[1])
        };
    }
    
    public static byte[] ComputeEnvelopeHash(MimeMessage message)
    {
        var hashInput = new StringBuilder();
        
        // Primary fields
        hashInput.Append(message.MessageId ?? "");
        hashInput.Append("|");
        hashInput.Append(message.From?.ToString() ?? "");
        hashInput.Append("|");
        hashInput.Append(message.To?.ToString() ?? "");
        hashInput.Append("|");
        hashInput.Append(message.Date.ToString("O"));
        hashInput.Append("|");
        hashInput.Append(message.Subject ?? "");
        hashInput.Append("|");
        
        // Additional fields for collision prevention
        hashInput.Append(message.Cc?.ToString() ?? "");
        hashInput.Append("|");
        hashInput.Append(message.InReplyTo ?? "");
        hashInput.Append("|");
        hashInput.Append(message.References?.FirstOrDefault() ?? "");
        hashInput.Append("|");
        
        // Size as differentiator
        var size = Encoding.UTF8.GetByteCount(message.ToString());
        hashInput.Append(size);
        
        return SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString()));
    }
    
    public static byte[] ComputeContentHash(byte[] emailData)
    {
        return SHA256.HashData(emailData);
    }
}