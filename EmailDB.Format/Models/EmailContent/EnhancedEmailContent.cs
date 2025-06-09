using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tenray.ZoneTree.Serializers;

public class EnhancedEmailContent 
{
    public byte[] Subject { get; set; }

    public string StrSubject
    {
        get => Subject is null || Subject.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(Subject);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                // Store an empty array instead of throwing an exception
                Subject = Array.Empty<byte>();
            }
            else
            {
                Subject = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public byte[] From { get; set; }

    public string StrFrom
    {
        get => From is null || From.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(From);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                From = Array.Empty<byte>();
            }
            else
            {
                From = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public byte[] To { get; set; }

    public string StrTo
    {
        get => To is null || To.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(To);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                To = Array.Empty<byte>();
            }
            else
            {
                To = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public byte[] Cc { get; set; }

    public string StrCc
    {
        get => Cc is null || Cc.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(Cc);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                Cc = Array.Empty<byte>();
            }
            else
            {
                Cc = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public byte[] Bcc { get; set; }

    public string StrBcc
    {
        get => Bcc is null || Bcc.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(Bcc);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                Bcc = Array.Empty<byte>();
            }
            else
            {
                Bcc = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public DateTime Date { get; set; }

    public byte[] TextContent { get; set; }

    public string StrTextContent
    {
        get => TextContent is null || TextContent.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(TextContent);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                TextContent = Array.Empty<byte>();
            }
            else
            {
                TextContent = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public byte[] AttachmentContent { get; set; }

    public string StrAttachmentContent
    {
        get => AttachmentContent is null || AttachmentContent.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(AttachmentContent);
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                AttachmentContent = Array.Empty<byte>();
            }
            else
            {
                AttachmentContent = Encoding.UTF8.GetBytes(value);
            }
        }
    }

    public DateTime ProcessedTime { get; set; }

    public int AttachmentCount { get; set; }
    public byte[] RawEmailContent { get; set; }

   
}