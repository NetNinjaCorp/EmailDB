using System.Text;
using EmailDB.Format;
using EmailDB.Format.Protobuf.V3;
using EmailDB.Format.V3;
using MimeKit;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the Tier 2/3 email block models and Preview extraction (US-EMDB-80-6, story
/// US-EMDB-80, docs/Folder_Listing.md Section 1):
/// <list type="bullet">
///   <item><see cref="EmailContent"/> (BlockType 7) carries the SHA3-256 <see cref="EmailHashedID"/>,
///   stable across sessions.</item>
///   <item><see cref="EmailMetadata"/> (BlockType 10) round-trips headers, MIME structure, threading
///   refs and Preview through the Zstd + Default-encryption pipeline.</item>
///   <item>Preview is extracted as plain text from HTML or text bodies.</item>
///   <item>Both block types compress with Zstd and encrypt under the Default policy on disk.</item>
/// </list>
/// </summary>
public class EmailBlockV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-emailblock-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();

    private const ushort ActiveEpoch = 2;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static EpochDekProvider MakeProvider()
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + 7);
        return new EpochDekProvider(
            FileId, ActiveEpoch, new[] { new EpochDekProvider.EpochDek(ActiveEpoch, dek) });
    }

    // A realistic multipart/mixed message: alternative text+HTML bodies, an attachment,
    // and threading headers (In-Reply-To + References). CRLF line endings per RFC 5322.
    private static byte[] SampleMime()
    {
        const string mime =
            "Message-Id: <child@example.com>\r\n" +
            "In-Reply-To: <parent@example.com>\r\n" +
            "References: <root@example.com> <parent@example.com>\r\n" +
            "From: Alice <alice@example.com>\r\n" +
            "To: Bob <bob@example.com>\r\n" +
            "Subject: Quarterly report\r\n" +
            "Date: Mon, 05 Jul 2021 10:00:00 +0000\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"OUTER\"\r\n" +
            "\r\n" +
            "--OUTER\r\n" +
            "Content-Type: multipart/alternative; boundary=\"INNER\"\r\n" +
            "\r\n" +
            "--INNER\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            "Hello Bob, the quarterly numbers look great.\r\n" +
            "--INNER\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "\r\n" +
            "<html><body><p>Hello Bob, the quarterly numbers look great.</p></body></html>\r\n" +
            "--INNER--\r\n" +
            "--OUTER\r\n" +
            "Content-Type: text/plain; name=\"notes.txt\"\r\n" +
            "Content-Disposition: attachment; filename=\"notes.txt\"\r\n" +
            "\r\n" +
            "line item detail\r\n" +
            "--OUTER--\r\n";
        return Encoding.ASCII.GetBytes(mime);
    }

    // ---- EmailHashedID content identity (story AC 1) -------------------------

    [Fact]
    public void EmailContent_CarriesSha3HashedId_StableAcrossBuilds()
    {
        var raw = SampleMime();
        var expected = EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(raw);

        var content1 = EmailModelBuilder.BuildContent(raw);
        var content2 = EmailModelBuilder.BuildContent(raw); // second "session"

        Assert.Equal(EmailDB.Format.V3.EmailHashedID.Size, content1.EmailHashedId.Length);
        Assert.Equal(expected.GetBytes(), content1.EmailHashedId);
        Assert.Equal(content1.EmailHashedId, content2.EmailHashedId); // stable
        Assert.Equal(raw, content1.RawContent);
    }

    [Fact]
    public void Metadata_And_Content_ShareTheSameHashedId()
    {
        var raw = SampleMime();
        var content = EmailModelBuilder.BuildContent(raw);
        var meta = EmailModelBuilder.BuildMetadata(raw);
        Assert.Equal(content.EmailHashedId, meta.EmailHashedId);
    }

    // ---- Preview extraction (story AC 3) ------------------------------------

    [Fact]
    public void Preview_FromTextBody_IsPlainText()
    {
        var preview = PreviewExtractor.Extract("Plain body   text\r\nsecond line", htmlBody: null);
        Assert.Equal("Plain body text second line", preview);
    }

    [Fact]
    public void Preview_FromHtmlBody_IsStrippedToPlainText()
    {
        var html =
            "<html><head><style>.x{color:red}</style></head>" +
            "<body><script>alert('x')</script><p>Hello&nbsp;<b>Bob</b>,</p><p>welcome</p></body></html>";
        var preview = PreviewExtractor.Extract(textBody: null, htmlBody: html);

        Assert.Equal("Hello Bob, welcome", preview);
        Assert.DoesNotContain("<", preview);
        Assert.DoesNotContain("alert", preview);   // script content dropped
        Assert.DoesNotContain("color:red", preview); // style content dropped
    }

    [Fact]
    public void Preview_TruncatedToMaxLength()
    {
        var longBody = new string('a', 500);
        var preview = PreviewExtractor.Extract(longBody, htmlBody: null);
        Assert.Equal(PreviewExtractor.MaxLength, preview.Length);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   \r\n\t ", null)]
    public void Preview_FromEmptyBody_IsEmpty(string? textBody, string? htmlBody)
    {
        Assert.Equal(string.Empty, PreviewExtractor.Extract(textBody, htmlBody));
    }

    [Fact]
    public void Preview_FromHtmlOnlyBody_FallsBackToStrippedHtml()
    {
        // No text/plain part: the preview must still be plain text derived from the HTML body.
        var html = "<div>Hello&nbsp;<b>Bob</b>, welcome &amp; enjoy.</div>";
        var preview = PreviewExtractor.Extract(textBody: null, htmlBody: html);
        Assert.Equal("Hello Bob, welcome & enjoy.", preview);
    }

    [Fact]
    public void Preview_ExtractedFromMessage_PrefersPlainText()
    {
        var meta = EmailModelBuilder.BuildMetadata(SampleMime());
        Assert.Equal("Hello Bob, the quarterly numbers look great.", meta.Preview);
    }

    // ---- Metadata round-trips through the store (story AC 2 + 4) -------------

    [Fact]
    public void Metadata_RoundTrips_Headers_Mime_Threading_Preview()
    {
        var raw = SampleMime();
        var contentId = Guid.NewGuid().ToByteArray(); // stand-in 16-byte ULID
        var meta = EmailModelBuilder.BuildMetadata(raw, contentId);

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EmailBlockStore(manager, provider, EncryptionPolicy.Default);

        var written = store.WriteMetadata(meta);
        Ok(written);

        var read = store.ReadMetadata(written.Value.Offset);
        Ok(read);
        var got = read.Value;

        // Content identity + Tier 3 link.
        Assert.Equal(meta.EmailHashedId, got.EmailHashedId);
        Assert.Equal(contentId, got.ContentBlockId);

        // Full header set, in order.
        Assert.Equal(meta.Headers.Count, got.Headers.Count);
        Assert.Contains(got.Headers, h => h.Name == "Subject" && h.Value == "Quarterly report");
        Assert.Equal(
            meta.Headers.Select(h => h.Name).ToArray(),
            got.Headers.Select(h => h.Name).ToArray());

        // Threading refs (MimeKit normalizes message ids without the angle brackets).
        Assert.Equal("child@example.com", got.MessageId);
        Assert.Equal("parent@example.com", got.InReplyTo);
        Assert.Equal(new[] { "root@example.com", "parent@example.com" }, got.References.ToArray());

        // Derived Tier 1 fields + Preview.
        Assert.Equal("Quarterly report", got.Subject);
        Assert.Contains("alice@example.com", got.From);
        Assert.Equal(raw.LongLength, got.MessageSize);
        Assert.Equal("Hello Bob, the quarterly numbers look great.", got.Preview);
        Assert.True(got.DateTicks > 0);

        // MIME structure: multipart/mixed → [ multipart/alternative → [text/plain, text/html],
        // text/plain attachment ].
        Assert.NotNull(got.MimeStructure);
        Assert.Equal("multipart/mixed", got.MimeStructure!.ContentType);
        Assert.Equal(2, got.MimeStructure.Children.Count);
        Assert.Equal("multipart/alternative", got.MimeStructure.Children[0].ContentType);
        Assert.Equal(2, got.MimeStructure.Children[0].Children.Count);
        var attachment = got.MimeStructure.Children[1];
        Assert.Equal("text/plain", attachment.ContentType);
        Assert.Equal("attachment", attachment.Disposition);
        Assert.Equal("notes.txt", attachment.FileName);
    }

    // A message with ordered duplicate Received headers (trace hops) and non-ASCII header
    // values (RFC 2047 encoded-words on Subject + From display name). MimeKit encodes on write.
    private static byte[] DuplicateHeadersNonAsciiMime()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Grüße Sénder", "sender@example.com"));
        message.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Subject = "Grüße café — 你好";
        message.MessageId = "nonascii@example.com";
        // Two Received hops, newest-first as an MTA would prepend them.
        message.Headers.Insert(0, new Header("Received",
            "from b.example.com by c.example.com; Mon, 05 Jul 2021 10:01:00 +0000"));
        message.Headers.Insert(0, new Header("Received",
            "from a.example.com by b.example.com; Mon, 05 Jul 2021 10:00:00 +0000"));
        message.Body = new TextPart("plain") { Text = "Body text" };

        using var ms = new MemoryStream();
        message.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Metadata_RoundTrips_OrderedDuplicateHeaders_And_NonAsciiValues()
    {
        var raw = DuplicateHeadersNonAsciiMime();
        var meta = EmailModelBuilder.BuildMetadata(raw);

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EmailBlockStore(manager, provider, EncryptionPolicy.Default);

        var written = store.WriteMetadata(meta);
        Ok(written);
        var read = store.ReadMetadata(written.Value.Offset);
        Ok(read);
        var got = read.Value;

        // Duplicate Received headers survive the round-trip, in original order.
        var received = got.Headers.Where(h => h.Name == "Received").Select(h => h.Value).ToArray();
        Assert.Equal(2, received.Length);
        Assert.Contains("from a.example.com", received[0]);
        Assert.Contains("from b.example.com", received[1]);

        // The full ordered header set (names and values) is byte-identical to pre-write.
        Assert.Equal(
            meta.Headers.Select(h => h.Name).ToArray(),
            got.Headers.Select(h => h.Name).ToArray());
        Assert.Equal(
            meta.Headers.Select(h => h.Value).ToArray(),
            got.Headers.Select(h => h.Value).ToArray());

        // Non-ASCII values round-trip and decode to their Unicode form.
        Assert.Equal("Grüße café — 你好", got.Subject);
        Assert.Contains("Grüße Sénder", got.From);
    }

    [Fact]
    public void Content_RoundTrips_ThroughStore()
    {
        var raw = SampleMime();
        var content = EmailModelBuilder.BuildContent(raw);

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EmailBlockStore(manager, provider, EncryptionPolicy.Default);

        var written = store.WriteContent(content);
        Ok(written);

        var read = store.ReadContent(written.Value.Offset);
        Ok(read);
        Assert.Equal(content.EmailHashedId, read.Value.EmailHashedId);
        Assert.Equal(raw, read.Value.RawContent);
    }

    // ---- Compress with Zstd + encrypt under Default (story AC 4) -------------

    [Theory]
    [InlineData(BlockType.EmailContent)]
    [InlineData(BlockType.EmailMetadata)]
    public void BothBlockTypes_CompressZstd_And_EncryptUnderDefault(BlockType type)
    {
        var raw = SampleMime();

        using var provider = MakeProvider();
        using var stream = OpenRW();
        using var manager = new BlockManager(stream, ownsStream: false);
        var store = new EmailBlockStore(manager, provider, EncryptionPolicy.Default);

        var written = type == BlockType.EmailContent
            ? store.WriteContent(EmailModelBuilder.BuildContent(raw))
            : store.WriteMetadata(EmailModelBuilder.BuildMetadata(raw));
        Ok(written);

        var block = manager.Read(written.Value.Offset);
        Ok(block);
        var header = block.Value.Header;

        Assert.Equal(type, header.Type);
        Assert.Equal(CompressionAlgorithm.Zstd, header.Compression);   // Zstd-compressed
        Assert.Equal(PayloadEncoding.Protobuf, header.Encoding);
        Assert.True(header.IsEncrypted);                               // encrypted under Default
        Assert.Equal(ActiveEpoch, header.KeyEpoch);                    // active epoch stamped
        // On-disk payload is ciphertext: the raw MIME must not appear verbatim.
        Assert.DoesNotContain("Quarterly report", Encoding.ASCII.GetString(block.Value.Payload));
    }
}
