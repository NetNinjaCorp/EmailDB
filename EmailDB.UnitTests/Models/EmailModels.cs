using System;
using System.Collections.Generic;

namespace EmailDB.UnitTests.Models
{
    /// <summary>
    /// Represents an email message for benchmarking purposes
    /// </summary>
    public class EmailMessage
    {
        /// <summary>
        /// Unique identifier for the email
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Email subject
        /// </summary>
        public string Subject { get; set; }

        /// <summary>
        /// Email body content
        /// </summary>
        public string Body { get; set; }

        /// <summary>
        /// Sender email address
        /// </summary>
        public string From { get; set; }

        /// <summary>
        /// List of recipient email addresses
        /// </summary>
        public List<string> To { get; set; } = new List<string>();

        /// <summary>
        /// List of CC recipient email addresses
        /// </summary>
        public List<string> Cc { get; set; } = new List<string>();

        /// <summary>
        /// List of BCC recipient email addresses
        /// </summary>
        public List<string> Bcc { get; set; } = new List<string>();

        /// <summary>
        /// Date and time when the email was sent
        /// </summary>
        public DateTime SentDate { get; set; }

        /// <summary>
        /// Date and time when the email was received
        /// </summary>
        public DateTime ReceivedDate { get; set; }

        /// <summary>
        /// Size of the email in bytes
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Flag indicating if the email has attachments
        /// </summary>
        public bool HasAttachments { get; set; }

        /// <summary>
        /// List of attachment information
        /// </summary>
        public List<EmailAttachment> Attachments { get; set; } = new List<EmailAttachment>();

        /// <summary>
        /// Flag indicating if the email has been read
        /// </summary>
        public bool IsRead { get; set; }

        /// <summary>
        /// Flag indicating if the email is flagged/starred
        /// </summary>
        public bool IsFlagged { get; set; }

        /// <summary>
        /// Current folder location of the email
        /// </summary>
        public string FolderPath { get; set; }
    }

    /// <summary>
    /// Represents an email attachment
    /// </summary>
    public class EmailAttachment
    {
        /// <summary>
        /// Filename of the attachment
        /// </summary>
        public string Filename { get; set; }

        /// <summary>
        /// MIME type of the attachment
        /// </summary>
        public string ContentType { get; set; }

        /// <summary>
        /// Size of the attachment in bytes
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// Content of the attachment (simplified as byte array for benchmarking)
        /// </summary>
        public byte[] Content { get; set; }
    }

    /// <summary>
    /// Represents an email folder
    /// </summary>
    public class EmailFolder
    {
        /// <summary>
        /// Name of the folder
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Full path of the folder
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Parent folder path
        /// </summary>
        public string ParentPath { get; set; }

        /// <summary>
        /// List of email IDs contained in this folder
        /// </summary>
        public List<string> EmailIds { get; set; } = new List<string>();

        /// <summary>
        /// Number of unread emails in the folder
        /// </summary>
        public int UnreadCount { get; set; }

        /// <summary>
        /// Total number of emails in the folder
        /// </summary>
        public int TotalCount => EmailIds.Count;
    }
}