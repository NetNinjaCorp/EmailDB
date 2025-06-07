//using MimeKit;
//using Tenray.ZoneTree;
//using EmailDB.Format.Models;
//using EmailDB.Format.ZoneTree;
//using System.Collections.Concurrent;
//using ZoneTree.FullTextSearch.SearchEngines;
//using ZoneTree.FullTextSearch.Index;
//using Tenray.ZoneTree.Options;

//namespace EmailDB.Format;

//public class EmailManager : IDisposable
//{
//    private readonly StorageManager storageManager;
//    private readonly IZoneTree<EmailHashedID, EnhancedEmailContent> emailIndex;
//    private readonly ConcurrentDictionary<string, EmailHashedID> emailCache;
//    private readonly HashedSearchEngine<EmailHashedID> emailSearchEngine;
//    private readonly FolderManager folderManager;
//    private readonly SegmentManager segmentManager;
//    private readonly object writeLock = new object();

//    public EmailManager(StorageManager storageManager, FolderManager folderManager, SegmentManager segmentManager)
//    {
//        this.storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
//        this.folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
//        this.segmentManager = segmentManager ?? throw new ArgumentNullException(nameof(segmentManager));

//        // Initialize ZoneTree for email indexing
//        var factory = new EmailDBZoneTreeFactory<EmailHashedID, EnhancedEmailContent>(storageManager);

//        if (!factory.CreateZoneTree("email_index")) { throw new Exception("Problem Creating Email Index"); }
//        this.emailIndex = factory.OpenOrCreate();
//        this.emailCache = new ConcurrentDictionary<string, EmailHashedID>();
//        var emailDBOptions = new AdvancedZoneTreeOptions<EmailHashedID, ulong>
//        {
//            FileStreamProvider = new EmailDBFileStreamProvider(segmentManager)
//        };
//        this.emailSearchEngine = new HashedSearchEngine<EmailHashedID>(
//            new IndexOfTokenRecordPreviousToken<EmailHashedID, ulong>(advancedOptions: emailDBOptions));
//    }

//    public async Task<EmailHashedID> AddEmailAsync(string emlFilePath, string folderName)
//    {
//        if (string.IsNullOrWhiteSpace(emlFilePath))
//            throw new ArgumentException("EML file path cannot be empty", nameof(emlFilePath));

//        if (string.IsNullOrWhiteSpace(folderName))
//            throw new ArgumentException("Folder name cannot be empty", nameof(folderName));

//        // Read and parse the email
//        var message = await ReadEmailFromFileAsync(emlFilePath);
//        if (message == null)
//            throw new InvalidOperationException("Failed to read email from file");

//        lock (writeLock)
//        {
//            // Generate EmailHashedID
//            var emailId = new EmailHashedID(message);

//            // Check if email already exists
//            if (emailIndex.TryGet(emailId, out var _))
//                throw new InvalidOperationException("Email already exists in the system");

//            // Create enhanced email content
//            var enhancedContent = CreateEnhancedEmailContent(message);

//            // Store in ZoneTree
//            emailIndex.TryAdd(emailId, enhancedContent, out long OpIndex);

//            // Add to folder
//            folderManager.AddEmailToFolder(folderName, emailId).RunSynchronously();

//            // Cache the email ID
//            emailCache[message.MessageId] = emailId;

//            return emailId;
//        }
//    }
//    public async Task<EmailHashedID> AddEmailAsync(byte[] emlBytes, string folderName)
//    {

//        if (string.IsNullOrWhiteSpace(folderName))
//            throw new ArgumentException("Folder name cannot be empty", nameof(folderName));

//        // Read and parse the email
//        var message = new MimeMessage(new MemoryStream(emlBytes));
//        if (message == null)
//            throw new InvalidOperationException("Failed to read email from file");

//        lock (writeLock)
//        {
//            // Generate EmailHashedID
//            var emailId = new EmailHashedID(message);

//            // Check if email already exists
//            if (emailIndex.TryGet(emailId, out var _))
//                throw new InvalidOperationException("Email already exists in the system");

//            // Create enhanced email content
//            var enhancedContent = CreateEnhancedEmailContent(message);

//            // Store in ZoneTree
//            emailIndex.TryAdd(emailId, enhancedContent, out long OpIndex);

//            // Add to folder
//            folderManager.AddEmailToFolder(folderName, emailId).RunSynchronously();

//            // Cache the email ID
//            emailCache[message.MessageId] = emailId;

//            return emailId;
//        }
//    }

//    public async Task<EnhancedEmailContent> GetEmailContentAsync(EmailHashedID emailId)
//    {
//        if (emailIndex.TryGet(emailId, out var content))
//            return content;

//        throw new KeyNotFoundException("Email not found");
//    }

//    public async Task<byte[]> GetRawEmailAsync(EmailHashedID emailId)
//    {
//        if (emailIndex.TryGet(emailId, out var content))
//        {
//            return content.RawEmailContent;
//        }
//        else { return null; }
//    }



//    public async Task<List<EmailHashedID>> SearchEmailsAsync(string searchTerm, SearchField field)
//    {
//        var results = new List<EmailHashedID>();

//        // Perform search through ZoneTree entries

//        //// Use the search engine to find matching email IDs


//        return results;
//    }

//    public async Task DeleteEmailAsync(EmailHashedID emailId, string folderName)
//    {
//        lock (writeLock)
//        {
//            // Remove from ZoneTree
//            if (emailIndex.TryDelete(emailId, out var res))
//            {

//            }

//            // Remove from folder           
//            folderManager.RemoveEmailFromFolder(folderName, emailId);

//            // Remove from cache
//            foreach (var key in emailCache.Where(x => x.Value.Equals(emailId)))
//            {
//                emailCache.TryRemove(key.Key, out _);
//            }
//        }
//    }

//    public async Task MoveEmailAsync(EmailHashedID emailId, string sourceFolder, string targetFolder)
//    {
//        storageManager.MoveEmail(emailId, sourceFolder, targetFolder);
//    }

//    private async Task<MimeMessage> ReadEmailFromFileAsync(string emlFilePath)
//    {
//        using var stream = File.OpenRead(emlFilePath);
//        return await MimeMessage.LoadAsync(stream);
//    }

//    private EnhancedEmailContent CreateEnhancedEmailContent(MimeMessage message)
//    {
//        return new EnhancedEmailContent
//        {
//            StrSubject = message.Subject ?? string.Empty,
//            StrFrom = message.From.ToString() ?? string.Empty,
//            StrTo = message.To.ToString() ?? string.Empty,
//            StrCc = message.Cc.ToString() ?? string.Empty,
//            StrBcc = message.Bcc.ToString() ?? string.Empty,
//            Date = message.Date.DateTime,
//            StrTextContent = GetTextContent(message),
//            AttachmentCount = message.Attachments.Count(),
//            ProcessedTime = DateTime.UtcNow,
//            // Store the raw content if needed for full reconstruction
//            RawEmailContent = GetRawContent(message)
//        };
//    }

//    private string GetTextContent(MimeMessage message)
//    {
//        return message.TextBody ?? string.Empty;
//    }

//    private byte[] GetRawContent(MimeMessage message)
//    {
//        using var ms = new MemoryStream();
//        message.WriteTo(ms);
//        return ms.ToArray();
//    }

//    public void Dispose()
//    {
//        emailIndex?.Dispose();
//        emailCache.Clear();
//    }
//}

//public enum SearchField
//{
//    Subject,
//    From,
//    To,
//    Content
//}