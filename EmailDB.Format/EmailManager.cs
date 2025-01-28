using MimeKit;
using Tenray.ZoneTree;
using EmailDB.Format.Models;
using EmailDB.Format.ZoneTree;
using System.Collections.Concurrent;

namespace EmailDB.Format;

public class EmailManager : IDisposable
{
    private readonly StorageManager storageManager;
    private readonly IZoneTree<EmailHashedID, EnhancedEmailContent> emailIndex;
    private readonly ConcurrentDictionary<string, EmailHashedID> emailCache;
    private readonly FolderManager folderManager;
    private readonly SegmentManager segmentManager;
    private readonly object writeLock = new object();

    public EmailManager(StorageManager storageManager, FolderManager folderManager, SegmentManager segmentManager)
    {
        this.storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        this.folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
        this.segmentManager = segmentManager ?? throw new ArgumentNullException(nameof(segmentManager));

        // Initialize ZoneTree for email indexing
        var factory = new EmailDBZoneTreeFactory<EmailHashedID, EnhancedEmailContent>(storageManager);
        this.emailIndex = factory.CreateZoneTree("email_index");
        this.emailCache = new ConcurrentDictionary<string, EmailHashedID>();
    }

    public async Task<EmailHashedID> AddEmailAsync(string emlFilePath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(emlFilePath))
            throw new ArgumentException("EML file path cannot be empty", nameof(emlFilePath));

        if (string.IsNullOrWhiteSpace(folderName))
            throw new ArgumentException("Folder name cannot be empty", nameof(folderName));

        // Read and parse the email
        var message = await ReadEmailFromFileAsync(emlFilePath);
        if (message == null)
            throw new InvalidOperationException("Failed to read email from file");

        lock (writeLock)
        {
            // Generate EmailHashedID
            var emailId = new EmailHashedID(message);

            // Check if email already exists
            if (emailIndex.TryGet(emailId, out var _))
                throw new InvalidOperationException("Email already exists in the system");

            // Create enhanced email content
            var enhancedContent = CreateEnhancedEmailContent(message);

            // Store in ZoneTree
            emailIndex.Put(emailId, enhancedContent);

            // Store the raw email content in segments
            using (var fileStream = File.OpenRead(emlFilePath))
            {
                var buffer = new byte[fileStream.Length];
                fileStream.Read(buffer, 0, buffer.Length);

                var segment = new SegmentContent
                {
                    SegmentId = BitConverter.ToUInt64(emailId.GetBytes(), 0), // Use first 8 bytes of hash as segment ID
                    SegmentData = buffer,
                    ContentLength = buffer.Length,
                    SegmentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Version = 1
                };

                var offset = segmentManager.WriteSegment(segment);

                // Update metadata
                storageManager.UpdateMetadata(metadata =>
                {
                    metadata.SegmentOffsets[emailId.ToString()] = offset;
                    return metadata;
                });
            }

            // Add to folder
            folderManager.AddEmailToFolder(folderName, BitConverter.ToUInt64(emailId.GetBytes(), 0));

            // Cache the email ID
            emailCache[message.MessageId] = emailId;

            return emailId;
        }
    }

    public async Task<EnhancedEmailContent> GetEmailContentAsync(EmailHashedID emailId)
    {
        if (emailIndex.TryGet(emailId, out var content))
            return content;

        throw new KeyNotFoundException("Email not found");
    }

    public async Task<byte[]> GetRawEmailAsync(EmailHashedID emailId)
    {
        var segmentId = BitConverter.ToUInt64(emailId.GetBytes(), 0);
        var segment = segmentManager.GetLatestSegment(segmentId);

        if (segment != null)
            return segment.SegmentData;

        throw new KeyNotFoundException("Raw email content not found");
    }

    public async Task<List<EmailHashedID>> SearchEmailsAsync(string searchTerm, SearchField field)
    {
        var results = new List<EmailHashedID>();

        // Perform search through ZoneTree entries
        foreach (var entry in emailIndex.GetEntries())
        {
            var content = entry.Value;
            bool matches = field switch
            {
                SearchField.Subject => content.StrSubject.Contains(searchTerm, StringComparison.OrdinalIgnoreCase),
                SearchField.From => content.StrFrom.Contains(searchTerm, StringComparison.OrdinalIgnoreCase),
                SearchField.To => content.StrTo.Contains(searchTerm, StringComparison.OrdinalIgnoreCase),
                SearchField.Content => content.StrTextContent.Contains(searchTerm, StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (matches)
                results.Add(entry.Key);
        }

        return results;
    }

    public async Task DeleteEmailAsync(EmailHashedID emailId, string folderName)
    {
        lock (writeLock)
        {
            // Remove from ZoneTree
            if(emailIndex.TryDelete(emailId, out var res))
            {

            }

            // Remove from folder
            var segmentId = BitConverter.ToUInt64(emailId.GetBytes(), 0);
            folderManager.RemoveEmailFromFolder(folderName, segmentId);

            // Mark segment as deleted
            var segmentOffsets = segmentManager.GetSegmentOffsets(segmentId);
            storageManager.UpdateMetadata(metadata =>
            {
                metadata.OutdatedOffsets.AddRange(segmentOffsets);
                return metadata;
            });

            // Remove from cache
            foreach (var key in emailCache.Where(x => x.Value.Equals(emailId)))
            {
                emailCache.TryRemove(key.Key, out _);
            }
        }
    }

    public async Task MoveEmailAsync(EmailHashedID emailId, string sourceFolder, string targetFolder)
    {
        var segmentId = BitConverter.ToUInt64(emailId.GetBytes(), 0);
        storageManager.MoveEmail(segmentId, sourceFolder, targetFolder);
    }

    private async Task<MimeMessage> ReadEmailFromFileAsync(string emlFilePath)
    {
        using var stream = File.OpenRead(emlFilePath);
        return await MimeMessage.LoadAsync(stream);
    }

    private EnhancedEmailContent CreateEnhancedEmailContent(MimeMessage message)
    {
        return new EnhancedEmailContent
        {
            StrSubject = message.Subject ?? string.Empty,
            StrFrom = message.From.ToString() ?? string.Empty,
            StrTo = message.To.ToString() ?? string.Empty,
            StrCc = message.Cc.ToString() ?? string.Empty,
            StrBcc = message.Bcc.ToString() ?? string.Empty,
            Date = message.Date.DateTime,
            StrTextContent = GetTextContent(message),
            AttachmentCount = message.Attachments.Count(),
            ProcessedTime = DateTime.UtcNow,
            // Store the raw content if needed for full reconstruction
            RawEmailContent = GetRawContent(message)
        };
    }

    private string GetTextContent(MimeMessage message)
    {
        return message.TextBody ?? string.Empty;
    }

    private byte[] GetRawContent(MimeMessage message)
    {
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        emailIndex?.Dispose();
        emailCache.Clear();
    }
}

public enum SearchField
{
    Subject,
    From,
    To,
    Content
}