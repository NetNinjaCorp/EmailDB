using MimeKit;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// Builds the Tier 2 (<see cref="EmailMetadata"/>) and Tier 3 (<see cref="EmailContent"/>) block
/// models from a raw RFC 5322 MIME message. Parsing is done once with MimeKit; the resulting
/// models carry the same <see cref="EmailHashedID"/> (SHA3-256 of the raw bytes), so the pair is
/// content-addressed and consistent.
///
/// <para>The raw MIME bytes are the canonical content — <see cref="EmailHashedID"/> hashes them
/// verbatim, never the parsed object — so identity stays stable even if MimeKit's parse or this
/// extraction logic changes.</para>
/// </summary>
public static class EmailModelBuilder
{
    /// <summary>
    /// Wraps raw MIME bytes as a Tier 3 <see cref="EmailContent"/>, stamping the cached
    /// <see cref="EmailHashedID"/> computed from those exact bytes.
    /// </summary>
    public static EmailContent BuildContent(ReadOnlySpan<byte> rawMime)
    {
        var id = EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(rawMime);
        return new EmailContent
        {
            EmailHashedId = id.GetBytes(),
            RawContent = rawMime.ToArray(),
        };
    }

    /// <summary>
    /// Parses raw MIME into a Tier 2 <see cref="EmailMetadata"/>: full ordered header set, MIME
    /// part structure, threading references, and the ~200-char <see cref="EmailMetadata.Preview"/>.
    /// The <see cref="EmailHashedID"/> is computed from the raw bytes (not the parse), matching the
    /// Tier 3 block.
    /// </summary>
    /// <param name="rawMime">The exact RFC 5322 message octets.</param>
    /// <param name="contentBlockId">
    /// Optional BlockId (ULID, 16 bytes) of the Tier 3 block, to link Tier 2 → Tier 3.
    /// </param>
    public static EmailMetadata BuildMetadata(byte[] rawMime, byte[]? contentBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(rawMime);

        var id = EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(rawMime);
        using var stream = new MemoryStream(rawMime, writable: false);
        var message = MimeMessage.Load(stream);

        var meta = new EmailMetadata
        {
            EmailHashedId = id.GetBytes(),
            ContentBlockId = contentBlockId is null ? Array.Empty<byte>() : (byte[])contentBlockId.Clone(),
            MessageId = message.MessageId ?? string.Empty,
            InReplyTo = message.InReplyTo ?? string.Empty,
            Subject = message.Subject ?? string.Empty,
            From = message.From?.ToString() ?? string.Empty,
            MessageSize = rawMime.LongLength,
            Preview = PreviewExtractor.Extract(message.TextBody, message.HtmlBody),
        };

        // Full RFC 5322 header set, in the order they appeared.
        foreach (var header in message.Headers)
            meta.Headers.Add(new EmailHeaderField { Name = header.Field, Value = header.Value });

        // Threading ancestry (oldest-first, as stored in the References header).
        if (message.References is not null)
            foreach (var reference in message.References)
                meta.References.Add(reference);

        // Send date, if present and parseable.
        if (message.Headers.Contains(HeaderId.Date))
            meta.DateTicks = message.Date.UtcDateTime.Ticks;

        // MIME part structure (shape only; bodies stay in Tier 3).
        meta.MimeStructure = message.Body is null ? null : BuildPartTree(message.Body);

        return meta;
    }

    private static MimePartNode BuildPartTree(MimeEntity entity)
    {
        var node = new MimePartNode
        {
            ContentType = entity.ContentType?.MimeType ?? string.Empty,
            Disposition = entity.ContentDisposition?.Disposition ?? string.Empty,
        };

        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart)
                    node.Children.Add(BuildPartTree(child));
                break;

            case MessagePart messagePart when messagePart.Message?.Body is not null:
                node.Children.Add(BuildPartTree(messagePart.Message.Body));
                break;

            case MimePart part:
                node.FileName = part.FileName ?? string.Empty;
                node.ContentId = part.ContentId ?? string.Empty;
                if (part.Content?.Stream is { CanSeek: true } content)
                    node.Size = content.Length;
                break;
        }

        return node;
    }
}
