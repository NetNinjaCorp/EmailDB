using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bogus;
using EmailDB.UnitTests.Helpers;
using EmailDB.UnitTests.Models;

namespace EmailDB.UnitTests.Benchmarks
{
    /// <summary>
    /// Benchmark class for email database operations
    /// </summary>
    public class EmailBenchmark : IDisposable
    {
        private readonly string _benchmarkDirectory;
        private readonly Random _random;
        private readonly Faker<EmailMessage> _emailFaker;
        private readonly Dictionary<string, EmailFolder> _folders;
        private readonly Dictionary<string, EmailMessage> _emails;
        private readonly TestBlockManager _blockManager;
        private readonly MockRawBlockManager _rawBlockManager;
        private readonly TestCacheManager _cacheManager;
        private readonly int _seed;
        private readonly Stopwatch _stopwatch;
        private readonly List<BenchmarkResult> _results;

        /// <summary>
        /// Initializes a new instance of the EmailBenchmark class
        /// </summary>
        /// <param name="seed">Random seed for reproducible results</param>
        /// <param name="benchmarkDirectory">Directory to store benchmark data</param>
        public EmailBenchmark(int seed = 42, string benchmarkDirectory = "benchmark_data")
        {
            _seed = seed;
            _random = new Random(seed);
            _benchmarkDirectory = Path.Combine(Directory.GetCurrentDirectory(), benchmarkDirectory);
            _stopwatch = new Stopwatch();
            _results = new List<BenchmarkResult>();
            _folders = new Dictionary<string, EmailFolder>();
            _emails = new Dictionary<string, EmailMessage>();

            // Create benchmark directory if it doesn't exist
            if (!Directory.Exists(_benchmarkDirectory))
            {
                Directory.CreateDirectory(_benchmarkDirectory);
            }

            // Initialize test managers
            _rawBlockManager = new MockRawBlockManager();
            _cacheManager = new TestCacheManager(_rawBlockManager);
            _blockManager = new TestBlockManager(_rawBlockManager, _cacheManager);

            // Setup Bogus faker for email generation
            _emailFaker = new Faker<EmailMessage>()
                .RuleFor(e => e.Id, f => Guid.NewGuid().ToString())
                .RuleFor(e => e.Subject, f => f.Lorem.Sentence())
                .RuleFor(e => e.Body, f => f.Lorem.Paragraphs(_random.Next(1, 5)))
                .RuleFor(e => e.From, f => f.Internet.Email())
                .RuleFor(e => e.To, f => new List<string> { f.Internet.Email(), f.Internet.Email() })
                .RuleFor(e => e.Cc, f => _random.Next(0, 3) > 0 ? new List<string> { f.Internet.Email() } : new List<string>())
                .RuleFor(e => e.Bcc, f => _random.Next(0, 2) > 0 ? new List<string> { f.Internet.Email() } : new List<string>())
                .RuleFor(e => e.SentDate, f => f.Date.Recent(30))
                .RuleFor(e => e.ReceivedDate, (f, e) => e.SentDate.AddMinutes(_random.Next(1, 10)))
                .RuleFor(e => e.HasAttachments, f => _random.NextDouble() < 0.3)
                .RuleFor(e => e.Attachments, (f, e) => e.HasAttachments ? GenerateAttachments(f, _random.Next(1, 4)) : new List<EmailAttachment>())
                .RuleFor(e => e.Size, (f, e) => CalculateEmailSize(e))
                .RuleFor(e => e.IsRead, f => _random.NextDouble() < 0.7)
                .RuleFor(e => e.IsFlagged, f => _random.NextDouble() < 0.1)
                .RuleFor(e => e.FolderPath, f => "/Inbox");

            // Create default folders
            CreateDefaultFolders();
        }

        /// <summary>
        /// Creates default email folders
        /// </summary>
        private void CreateDefaultFolders()
        {
            var defaultFolders = new[]
            {
                "/Inbox",
                "/Sent",
                "/Drafts",
                "/Trash",
                "/Archive",
                "/Work",
                "/Personal",
                "/Work/Projects",
                "/Work/Meetings",
                "/Personal/Family",
                "/Personal/Friends"
            };

            foreach (var path in defaultFolders)
            {
                CreateFolder(path);
            }
        }

        /// <summary>
        /// Creates a folder at the specified path
        /// </summary>
        /// <param name="path">Folder path</param>
        /// <returns>The created folder</returns>
        public EmailFolder CreateFolder(string path)
        {
            if (_folders.ContainsKey(path))
            {
                return _folders[path];
            }

            var parts = path.Split('/').Where(p => !string.IsNullOrEmpty(p)).ToArray();
            var name = parts.Last();
            var parentPath = parts.Length > 1 ? "/" + string.Join("/", parts.Take(parts.Length - 1)) : "";

            // Create parent folder if it doesn't exist
            if (!string.IsNullOrEmpty(parentPath) && !_folders.ContainsKey(parentPath))
            {
                CreateFolder(parentPath);
            }

            var folder = new EmailFolder
            {
                Name = name,
                Path = path,
                ParentPath = parentPath
            };

            _folders[path] = folder;
            return folder;
        }

        /// <summary>
        /// Generates email attachments
        /// </summary>
        /// <param name="faker">Faker instance</param>
        /// <param name="count">Number of attachments to generate</param>
        /// <returns>List of email attachments</returns>
        private List<EmailAttachment> GenerateAttachments(Faker faker, int count)
        {
            var attachments = new List<EmailAttachment>();
            var fileExtensions = new[] { ".pdf", ".docx", ".xlsx", ".jpg", ".png", ".txt" };

            for (int i = 0; i < count; i++)
            {
                var extension = fileExtensions[_random.Next(fileExtensions.Length)];
                var contentType = GetContentTypeForExtension(extension);
                var size = extension switch
                {
                    ".pdf" => _random.Next(100_000, 5_000_000),
                    ".docx" => _random.Next(50_000, 2_000_000),
                    ".xlsx" => _random.Next(20_000, 1_000_000),
                    ".jpg" => _random.Next(200_000, 4_000_000),
                    ".png" => _random.Next(100_000, 3_000_000),
                    ".txt" => _random.Next(1_000, 100_000),
                    _ => _random.Next(10_000, 1_000_000)
                };

                var attachment = new EmailAttachment
                {
                    Filename = faker.System.FileName(extension),
                    ContentType = contentType,
                    Size = size,
                    // For benchmarking, we don't need actual content, just simulate the size
                    Content = new byte[Math.Min(size, 1024)] // Limit actual memory usage
                };

                attachments.Add(attachment);
            }

            return attachments;
        }

        /// <summary>
        /// Gets the MIME content type for a file extension
        /// </summary>
        /// <param name="extension">File extension</param>
        /// <returns>MIME content type</returns>
        private string GetContentTypeForExtension(string extension)
        {
            return extension switch
            {
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".jpg" => "image/jpeg",
                ".png" => "image/png",
                ".txt" => "text/plain",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Calculates the size of an email based on its content
        /// </summary>
        /// <param name="email">Email message</param>
        /// <returns>Size in bytes</returns>
        private long CalculateEmailSize(EmailMessage email)
        {
            long size = 0;

            // Headers
            size += email.Subject?.Length * 2 ?? 0;
            size += email.From?.Length * 2 ?? 0;
            
            // Recipients
            foreach (var recipient in email.To)
            {
                size += recipient.Length * 2;
            }
            
            foreach (var recipient in email.Cc)
            {
                size += recipient.Length * 2;
            }
            
            foreach (var recipient in email.Bcc)
            {
                size += recipient.Length * 2;
            }

            // Body
            size += email.Body?.Length * 2 ?? 0;

            // Attachments
            foreach (var attachment in email.Attachments)
            {
                size += attachment.Size;
            }

            return size;
        }

        /// <summary>
        /// Adds emails to the database
        /// </summary>
        /// <param name="count">Number of emails to add</param>
        /// <param name="folderPath">Target folder path</param>
        /// <returns>List of added email IDs</returns>
        public List<string> AddEmails(int count, string folderPath = "/Inbox")
        {
            if (!_folders.ContainsKey(folderPath))
            {
                CreateFolder(folderPath);
            }

            var folder = _folders[folderPath];
            var addedEmails = new List<string>();

            _stopwatch.Restart();

            for (int i = 0; i < count; i++)
            {
                var email = _emailFaker.Generate();
                email.FolderPath = folderPath;
                
                _emails[email.Id] = email;
                folder.EmailIds.Add(email.Id);
                
                if (!email.IsRead)
                {
                    folder.UnreadCount++;
                }
                
                addedEmails.Add(email.Id);
            }

            _stopwatch.Stop();

            // Record benchmark result
            _results.Add(new BenchmarkResult
            {
                Operation = "AddEmails",
                Count = count,
                FolderPath = folderPath,
                ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                DatabaseSize = CalculateDatabaseSize(),
                EmailCount = _emails.Count,
                FolderCount = _folders.Count
            });

            return addedEmails;
        }

        /// <summary>
        /// Moves emails between folders
        /// </summary>
        /// <param name="emailIds">List of email IDs to move</param>
        /// <param name="targetFolderPath">Target folder path</param>
        public void MoveEmails(List<string> emailIds, string targetFolderPath)
        {
            if (!_folders.ContainsKey(targetFolderPath))
            {
                CreateFolder(targetFolderPath);
            }

            var targetFolder = _folders[targetFolderPath];
            
            _stopwatch.Restart();

            foreach (var emailId in emailIds)
            {
                if (_emails.TryGetValue(emailId, out var email))
                {
                    var sourceFolder = _folders[email.FolderPath];
                    
                    // Remove from source folder
                    sourceFolder.EmailIds.Remove(emailId);
                    if (!email.IsRead)
                    {
                        sourceFolder.UnreadCount--;
                    }
                    
                    // Add to target folder
                    targetFolder.EmailIds.Add(emailId);
                    if (!email.IsRead)
                    {
                        targetFolder.UnreadCount++;
                    }
                    
                    // Update email's folder path
                    email.FolderPath = targetFolderPath;
                }
            }

            _stopwatch.Stop();

            // Record benchmark result
            _results.Add(new BenchmarkResult
            {
                Operation = "MoveEmails",
                Count = emailIds.Count,
                FolderPath = targetFolderPath,
                ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                DatabaseSize = CalculateDatabaseSize(),
                EmailCount = _emails.Count,
                FolderCount = _folders.Count
            });
        }

        /// <summary>
        /// Deletes emails (moves them to Trash)
        /// </summary>
        /// <param name="emailIds">List of email IDs to delete</param>
        public void DeleteEmails(List<string> emailIds)
        {
            MoveEmails(emailIds, "/Trash");
        }

        /// <summary>
        /// Permanently removes emails from the database
        /// </summary>
        /// <param name="emailIds">List of email IDs to remove</param>
        public void PermanentlyDeleteEmails(List<string> emailIds)
        {
            _stopwatch.Restart();

            foreach (var emailId in emailIds)
            {
                if (_emails.TryGetValue(emailId, out var email))
                {
                    var folder = _folders[email.FolderPath];
                    
                    // Remove from folder
                    folder.EmailIds.Remove(emailId);
                    if (!email.IsRead)
                    {
                        folder.UnreadCount--;
                    }
                    
                    // Remove from emails dictionary
                    _emails.Remove(emailId);
                }
            }

            _stopwatch.Stop();

            // Record benchmark result
            _results.Add(new BenchmarkResult
            {
                Operation = "PermanentlyDeleteEmails",
                Count = emailIds.Count,
                ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                DatabaseSize = CalculateDatabaseSize(),
                EmailCount = _emails.Count,
                FolderCount = _folders.Count
            });
        }

        /// <summary>
        /// Marks emails as read
        /// </summary>
        /// <param name="emailIds">List of email IDs to mark as read</param>
        public void MarkEmailsAsRead(List<string> emailIds)
        {
            _stopwatch.Restart();

            foreach (var emailId in emailIds)
            {
                if (_emails.TryGetValue(emailId, out var email) && !email.IsRead)
                {
                    email.IsRead = true;
                    
                    var folder = _folders[email.FolderPath];
                    folder.UnreadCount--;
                }
            }

            _stopwatch.Stop();

            // Record benchmark result
            _results.Add(new BenchmarkResult
            {
                Operation = "MarkEmailsAsRead",
                Count = emailIds.Count,
                ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                DatabaseSize = CalculateDatabaseSize(),
                EmailCount = _emails.Count,
                FolderCount = _folders.Count
            });
        }

        /// <summary>
        /// Marks emails as unread
        /// </summary>
        /// <param name="emailIds">List of email IDs to mark as unread</param>
        public void MarkEmailsAsUnread(List<string> emailIds)
        {
            _stopwatch.Restart();

            foreach (var emailId in emailIds)
            {
                if (_emails.TryGetValue(emailId, out var email) && email.IsRead)
                {
                    email.IsRead = false;
                    
                    var folder = _folders[email.FolderPath];
                    folder.UnreadCount++;
                }
            }

            _stopwatch.Stop();

            // Record benchmark result
            _results.Add(new BenchmarkResult
            {
                Operation = "MarkEmailsAsUnread",
                Count = emailIds.Count,
                ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                DatabaseSize = CalculateDatabaseSize(),
                EmailCount = _emails.Count,
                FolderCount = _folders.Count
            });
        }

        /// <summary>
        /// Searches for emails matching the specified criteria
        /// </summary>
        /// <param name="searchTerm">Search term</param>
        /// <param name="folderPath">Folder path to search in (null for all folders)</param>
        /// <returns>List of matching email IDs</returns>
        public List<string> SearchEmails(string searchTerm, string folderPath = null)
        {
            _stopwatch.Restart();

            var results = new List<string>();
            var searchTermLower = searchTerm.ToLowerInvariant();

            foreach (var email in _emails.Values)
            {
                // Filter by folder if specified
                if (folderPath != null && email.FolderPath != folderPath)
                {
                    continue;
                }

                // Search in subject, body, and sender
                if ((email.Subject?.ToLowerInvariant().Contains(searchTermLower) ?? false) ||
                    (email.Body?.ToLowerInvariant().Contains(searchTermLower) ?? false) ||
                    (email.From?.ToLowerInvariant().Contains(searchTermLower) ?? false))
                {
                    results.Add(email.Id);
                }
            }

            _stopwatch.Stop();

            // Record benchmark result
            _results.Add(new BenchmarkResult
            {
                Operation = "SearchEmails",
                SearchTerm = searchTerm,
                FolderPath = folderPath,
                Count = results.Count,
                ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                DatabaseSize = CalculateDatabaseSize(),
                EmailCount = _emails.Count,
                FolderCount = _folders.Count
            });

            return results;
        }

        /// <summary>
        /// Calculates the total size of the database
        /// </summary>
        /// <returns>Size in bytes</returns>
        public long CalculateDatabaseSize()
        {
            long size = 0;

            // Email content size
            foreach (var email in _emails.Values)
            {
                size += email.Size;
            }

            // Folder structure overhead (simplified estimate)
            size += _folders.Count * 100;

            // Index overhead (simplified estimate)
            size += _emails.Count * 50;

            return size;
        }

        /// <summary>
        /// Gets all benchmark results
        /// </summary>
        /// <returns>List of benchmark results</returns>
        public List<BenchmarkResult> GetResults()
        {
            return _results;
        }

        /// <summary>
        /// Runs a realistic email usage scenario with randomized steps based on seed
        /// </summary>
        /// <param name="seed">Random seed for reproducibility</param>
        public void RunRealisticScenario(int? seed = null)
        {
            var random = seed.HasValue ? new Random(seed.Value) : _random;

            // List of available folders for operations
            var availableFolders = new List<string>
            {
                "/Inbox",
                "/Sent",
                "/Drafts",
                "/Trash",
                "/Archive",
                "/Work",
                "/Personal",
                "/Work/Projects",
                "/Work/Meetings",
                "/Personal/Family",
                "/Personal/Friends"
            };

            // Add initial batch of emails
            var inboxEmails = AddEmails(200, "/Inbox");
            var allEmails = new List<string>(inboxEmails);

            // Determine number of operations based on seed
            int operationCount = random.Next(15, random.Next(30,1000));
            
            for (int i = 0; i < operationCount; i++)
            {
                // Select a random operation
                int operation = random.Next(7);
                
                switch (operation)
                {
                    case 0: // Add emails to a random folder
                        {
                            string folder = availableFolders[random.Next(availableFolders.Count)];
                            int count = random.Next(10, random.Next(10,1000));
                            var newEmails = AddEmails(count, folder);
                            allEmails.AddRange(newEmails);
                            break;
                        }
                    case 1: // Move emails to a random folder
                        {
                            if (allEmails.Count > 0)
                            {
                                string targetFolder = availableFolders[random.Next(availableFolders.Count)];
                                int count = Math.Min(random.Next(5,500), allEmails.Count);
                                var emailsToMove = allEmails.OrderBy(x => random.Next()).Take(count).ToList();
                                MoveEmails(emailsToMove, targetFolder);
                            }
                            break;
                        }
                    case 2: // Delete emails (move to trash)
                        {
                            if (allEmails.Count > 0)
                            {
                                int count = Math.Min(random.Next(5, 500), allEmails.Count);
                                var emailsToDelete = allEmails.OrderBy(x => random.Next()).Take(count).ToList();
                                DeleteEmails(emailsToDelete);
                            }
                            break;
                        }
                    case 3: // Permanently delete emails
                        {
                            if (allEmails.Count > 0)
                            {
                                int count = Math.Min(random.Next(5, 500), allEmails.Count);
                                var emailsToDelete = allEmails.OrderBy(x => random.Next()).Take(count).ToList();
                                PermanentlyDeleteEmails(emailsToDelete);
                                // Remove from our tracking list
                                foreach (var email in emailsToDelete)
                                {
                                    allEmails.Remove(email);
                                }
                            }
                            break;
                        }
                    case 4: // Mark emails as read
                        {
                            if (allEmails.Count > 0)
                            {
                                int count = Math.Min(random.Next(10, 1000), allEmails.Count);
                                var emailsToMark = allEmails.OrderBy(x => random.Next()).Take(count).ToList();
                                MarkEmailsAsRead(emailsToMark);
                            }
                            break;
                        }
                    case 5: // Mark emails as unread
                        {
                            if (allEmails.Count > 0)
                            {
                                int count = Math.Min(random.Next(5, 1000), allEmails.Count);
                                var emailsToMark = allEmails.OrderBy(x => random.Next()).Take(count).ToList();
                                MarkEmailsAsUnread(emailsToMark);
                            }
                            break;
                        }
                    case 6: // Search emails
                        {
                            string[] searchTerms = { "meeting", "important", "project", "report", "update", "family", "urgent" };
                            string searchTerm = searchTerms[random.Next(searchTerms.Length)];
                            string folder = random.Next(2) == 0 ? availableFolders[random.Next(availableFolders.Count)] : null;
                            SearchEmails(searchTerm, folder);
                            break;
                        }
                }
            }
            
            // Output final statistics
            long totalSize = CalculateDatabaseSize();
            Console.WriteLine("\nRealistic Benchmark Summary:");
            Console.WriteLine($"Total Emails: {_emails.Count:N0}");
            Console.WriteLine($"Total Size: {FormatSize(totalSize)}");
            Console.WriteLine($"Emails per Folder:");
            
            foreach (var folder in _folders.Values.OrderBy(f => f.Path))
            {
                if (folder.EmailIds.Count > 0)
                {
                    Console.WriteLine($"  {folder.Path}: {folder.EmailIds.Count:N0} emails ({folder.UnreadCount:N0} unread)");
                }
            }
        }

        /// <summary>
        /// Runs a large-scale benchmark with the specified number of emails
        /// </summary>
        /// <param name="emailCount">Number of emails to add</param>
        public void RunLargeScaleBenchmark(int emailCount)
        {
            // Add emails in batches
            int batchSize = 1000;
            int remainingEmails = emailCount;

            while (remainingEmails > 0)
            {
                int currentBatch = Math.Min(batchSize, remainingEmails);
                AddEmails(currentBatch, "/Inbox");
                remainingEmails -= currentBatch;
            }

            // Perform some operations to simulate usage
            var sampleEmails = _emails.Keys.Take(Math.Min(1000, emailCount / 10)).ToList();
            
            // Mark some as read
            MarkEmailsAsRead(sampleEmails.Take(sampleEmails.Count * 7 / 10).ToList());
            
            // Move some to different folders
            MoveEmails(sampleEmails.Take(sampleEmails.Count / 5).ToList(), "/Archive");
            MoveEmails(sampleEmails.Skip(sampleEmails.Count / 5).Take(sampleEmails.Count / 5).ToList(), "/Work");
            MoveEmails(sampleEmails.Skip(2 * sampleEmails.Count / 5).Take(sampleEmails.Count / 5).ToList(), "/Personal");
            
            // Delete some
            DeleteEmails(sampleEmails.Skip(3 * sampleEmails.Count / 5).Take(sampleEmails.Count / 5).ToList());
            
            // Search
            SearchEmails("important");
        }

        /// <summary>
        /// Runs an absurdly large benchmark with 1 million emails
        /// </summary>
        public void RunAbsurdlyLargeBenchmark()
        {
            const int emailCount = 1_000_000;
            
            Console.WriteLine($"Starting absurdly large benchmark with {emailCount:N0} emails...");
            Console.WriteLine("This may take a significant amount of time to complete.");
            
            // Add emails in larger batches for efficiency
            int batchSize = 5000;
            int remainingEmails = emailCount;
            int batchesCompleted = 0;
            int totalBatches = (int)Math.Ceiling(emailCount / (double)batchSize);

            while (remainingEmails > 0)
            {
                int currentBatch = Math.Min(batchSize, remainingEmails);
                AddEmails(currentBatch, "/Inbox");
                remainingEmails -= currentBatch;
                
                batchesCompleted++;
                if (batchesCompleted % 20 == 0 || remainingEmails == 0)
                {
                    Console.WriteLine($"Progress: {batchesCompleted}/{totalBatches} batches completed ({emailCount - remainingEmails:N0}/{emailCount:N0} emails)");
                }
            }

            Console.WriteLine("All emails added. Performing operations on a sample...");
            
            // For operations, we'll use a smaller sample to keep execution time reasonable
            var sampleEmails = _emails.Keys.Take(10000).ToList();
            
            // Mark some as read
            Console.WriteLine("Marking emails as read...");
            MarkEmailsAsRead(sampleEmails.Take(7000).ToList());
            
            // Move some to different folders
            Console.WriteLine("Moving emails between folders...");
            MoveEmails(sampleEmails.Take(2000).ToList(), "/Archive");
            MoveEmails(sampleEmails.Skip(2000).Take(2000).ToList(), "/Work");
            MoveEmails(sampleEmails.Skip(4000).Take(2000).ToList(), "/Personal");
            
            // Delete some
            Console.WriteLine("Deleting emails...");
            DeleteEmails(sampleEmails.Skip(6000).Take(2000).ToList());
            
            // Search
            Console.WriteLine("Performing search operations...");
            SearchEmails("important");
            SearchEmails("meeting", "/Work");
            
            // Output final statistics
            long totalSize = CalculateDatabaseSize();
            Console.WriteLine("\nAbsurdly Large Benchmark Summary:");
            Console.WriteLine($"Total Emails: {_emails.Count:N0}");
            Console.WriteLine($"Total Size: {FormatSize(totalSize)}");
            Console.WriteLine($"Emails per Folder:");
            
            foreach (var folder in _folders.Values.OrderBy(f => f.Path))
            {
                if (folder.EmailIds.Count > 0)
                {
                    Console.WriteLine($"  {folder.Path}: {folder.EmailIds.Count:N0} emails ({folder.UnreadCount:N0} unread)");
                }
            }
            
            Console.WriteLine("Absurdly large benchmark completed.");
        }

        /// <summary>
        /// Generates a report of benchmark results
        /// </summary>
        /// <returns>Benchmark report as a string</returns>
        public string GenerateReport()
        {
            var report = new System.Text.StringBuilder();
            
            report.AppendLine("Email Database Benchmark Report");
            report.AppendLine("==============================");
            report.AppendLine();
            
            report.AppendLine($"Seed: {_seed}");
            report.AppendLine($"Total Emails: {_emails.Count}");
            report.AppendLine($"Total Folders: {_folders.Count}");
            report.AppendLine($"Database Size: {FormatSize(CalculateDatabaseSize())}");
            report.AppendLine();
            
            report.AppendLine("Operation Results:");
            report.AppendLine("------------------");
            
            foreach (var result in _results)
            {
                report.AppendLine($"- {result.Operation}:");
                report.AppendLine($"  Count: {result.Count}");
                
                if (!string.IsNullOrEmpty(result.FolderPath))
                {
                    report.AppendLine($"  Folder: {result.FolderPath}");
                }
                
                if (!string.IsNullOrEmpty(result.SearchTerm))
                {
                    report.AppendLine($"  Search Term: {result.SearchTerm}");
                }
                
                report.AppendLine($"  Time: {result.ElapsedMilliseconds} ms");
                report.AppendLine($"  Database Size: {FormatSize(result.DatabaseSize)}");
                report.AppendLine();
            }
            
            return report.ToString();
        }

        /// <summary>
        /// Formats a size in bytes to a human-readable string
        /// </summary>
        /// <param name="bytes">Size in bytes</param>
        /// <returns>Formatted size string</returns>
        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;
            
            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }
            
            return $"{size:F2} {suffixes[suffixIndex]}";
        }

        /// <summary>
        /// Saves the benchmark report to a file
        /// </summary>
        /// <param name="filename">Output filename</param>
        public void SaveReportToFile(string filename = "benchmark_report.txt")
        {
            var report = GenerateReport();
            var path = Path.Combine(_benchmarkDirectory, filename);
            File.WriteAllText(path, report);
        }

        /// <summary>
        /// Disposes resources used by the benchmark
        /// </summary>
        public void Dispose()
        {
            // Clean up resources
        }
    }

    /// <summary>
    /// Represents a benchmark result
    /// </summary>
    public class BenchmarkResult
    {
        /// <summary>
        /// Operation name
        /// </summary>
        public string Operation { get; set; }
        
        /// <summary>
        /// Number of items processed
        /// </summary>
        public int Count { get; set; }
        
        /// <summary>
        /// Folder path (if applicable)
        /// </summary>
        public string FolderPath { get; set; }
        
        /// <summary>
        /// Search term (if applicable)
        /// </summary>
        public string SearchTerm { get; set; }
        
        /// <summary>
        /// Elapsed time in milliseconds
        /// </summary>
        public long ElapsedMilliseconds { get; set; }
        
        /// <summary>
        /// Database size in bytes
        /// </summary>
        public long DatabaseSize { get; set; }
        
        /// <summary>
        /// Total number of emails
        /// </summary>
        public int EmailCount { get; set; }
        
        /// <summary>
        /// Total number of folders
        /// </summary>
        public int FolderCount { get; set; }
    }
}