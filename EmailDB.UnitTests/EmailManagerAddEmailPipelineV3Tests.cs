using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="EmailManager.AddEmail"/> (US-EMDB-85-5): the single-email write pipeline
/// that persists an email end-to-end — EmailHashedID dedupe, Tier 3 <c>EmailContent</c> + Tier 2
/// <c>EmailMetadata</c> block writes, a WAL entry, PrimaryEmail + Date index buffer inserts, and a
/// folder delta append (EmailDB_FileFormat_Spec.md Sections 5-11, docs/Folder_Listing.md).
///
/// <para>Each of the five DoD pipeline steps is asserted directly: the blocks are written and read
/// back (plaintext and encrypted); duplicate content is detected via the content hash and not stored
/// twice; the WAL sequence advances; the primary and date indexes observe the email (read-your-writes);
/// and the folder directory advances with a readable Add delta.</para>
/// </summary>
public class EmailManagerAddEmailPipelineV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-addemail-{Guid.NewGuid():N}.emdb");

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) keep the KDF fast in tests.
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(string subject, string body) =>
        Encoding.UTF8.GetBytes(
            $"From: sender@example.com\r\nTo: rcpt@example.com\r\nSubject: {subject}\r\n\r\n{body}");

    private static FolderPageDirectory NewFolder() =>
        FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

    private static AddEmailRequest Request(FolderPageDirectory folder, byte[] mime, long ticks) => new()
    {
        RawContent = mime,
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2-metadata-payload"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = "sender@example.com",
        Subject = "probe",
        Preview = "preview body text",
    };

    /// <summary>Resolves an appended block's live offset through the composed resolver (runtime map → location index).</summary>
    private static long OffsetOf(EmailManager mgr, byte[] blockId)
    {
        Assert.True(mgr.Resolver!.TryGetLocation(blockId, out var loc) && loc is not null,
            "appended block did not resolve to a file offset");
        return loc!.Offset;
    }

    // --------------------------------------------------------- Full pipeline (plaintext)

    [Fact]
    public void AddEmail_writes_tier3_and_tier2_blocks_and_buffers_both_index_inserts()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Hello", "the raw MIME body bytes");
        long ticks = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;

        var added = mgr.AddEmail(Request(folder, mime, ticks));
        Ok(added);
        var r = added.Value;

        Assert.False(r.IsDuplicate);
        Assert.Equal(EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(mime), r.EmailId);
        Assert.NotNull(r.ContentBlockId);
        Assert.NotNull(r.MetadataBlockId);

        // Tier 3 EmailContent block holds the raw MIME verbatim (plaintext file).
        var content = mgr.BlockManager.Read(OffsetOf(mgr, r.ContentBlockId!));
        Ok(content);
        Assert.Equal(BlockType.EmailContent, content.Value.Header.Type);
        Assert.False(content.Value.Header.IsEncrypted);
        Assert.Equal(mime, content.Value.Payload);

        // Tier 2 EmailMetadata block holds the metadata payload.
        var meta = mgr.BlockManager.Read(OffsetOf(mgr, r.MetadataBlockId!));
        Ok(meta);
        Assert.Equal(BlockType.EmailMetadata, meta.Value.Header.Type);

        // Primary index buffered insert: EmailHashedID → ContentBlockId (read-your-writes).
        var lookup = mgr.PrimaryIndex!.TryGet(r.EmailId.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found);
        Assert.Equal(r.ContentBlockId, lookup.Value.Value);

        // Date index insert: the timestamp resolves back to the ContentBlockId.
        var range = mgr.DateIndex!.RangeQuery(ticks, ticks);
        Ok(range);
        Assert.Single(range.Value);
        Assert.Equal(ticks, range.Value[0].DateTicks);
        Assert.Equal(r.ContentBlockId, range.Value[0].BlockId);
    }

    // --------------------------------------------------------- Dedupe

    [Fact]
    public void AddEmail_detects_duplicate_content_via_hash_and_does_not_store_twice()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Dup", "identical content bytes");

        var first = mgr.AddEmail(Request(folder, mime, 1000));
        Ok(first);
        Assert.False(first.Value.IsDuplicate);
        Assert.Equal(1, mgr.PrimaryIndex!.PendingCount);

        // Re-adding the exact same MIME is caught by the content hash: no new content block,
        // no additional buffered index entry — the same identity is reported.
        var second = mgr.AddEmail(Request(folder.Rewrite(folder.PageEntries, folder.HeadDeltaBlockId), mime, 2000));
        Ok(second);
        Assert.True(second.Value.IsDuplicate);
        Assert.Null(second.Value.ContentBlockId);
        Assert.Equal(first.Value.EmailId, second.Value.EmailId);
        Assert.Equal(1, mgr.PrimaryIndex.PendingCount); // still exactly one buffered insert
    }

    [Fact]
    public void AddEmail_duplicate_appends_no_new_blocks_and_leaves_file_length_and_indexes_unchanged()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Dup", "identical content bytes");

        var first = mgr.AddEmail(Request(folder, mime, 1000));
        Ok(first);
        Assert.False(first.Value.IsDuplicate);

        // Snapshot the physical + logical state after the one genuine add. Flush is a pure fsync
        // (no appends), so the on-disk file length reflects every block this add wrote.
        Assert.True(mgr.BlockManager.Flush().IsSuccess);
        long lengthAfterFirst = new FileInfo(_path).Length;
        int pendingAfterFirst = mgr.PrimaryIndex!.PendingCount;
        var dateAfterFirst = mgr.DateIndex!.RangeQuery(0, long.MaxValue);
        Ok(dateAfterFirst);
        int dateCountAfterFirst = dateAfterFirst.Value.Count;
        var folderAfterFirst = first.Value.Folder!;

        // Re-add the identical bytes onto the advanced folder: the content hash catches it up front.
        var second = mgr.AddEmail(Request(folderAfterFirst, mime, 2000));
        Ok(second);
        Assert.True(second.Value.IsDuplicate);

        // No block ids handed back at all — no Tier 3, Tier 2, WAL, or folder-delta work was done.
        Assert.Null(second.Value.ContentBlockId);
        Assert.Null(second.Value.MetadataBlockId);
        Assert.Null(second.Value.WalSequence);
        Assert.Null(second.Value.DeltaBlockId);

        // Physically nothing was appended: the file is byte-for-byte the same length as before.
        Assert.True(mgr.BlockManager.Flush().IsSuccess);
        Assert.Equal(lengthAfterFirst, new FileInfo(_path).Length);

        // No spurious index or folder work either: the primary buffer, the date index entry count,
        // and the folder directory version are all exactly as they were before the duplicate attempt.
        Assert.Equal(pendingAfterFirst, mgr.PrimaryIndex.PendingCount);
        var dateAfterSecond = mgr.DateIndex.RangeQuery(0, long.MaxValue);
        Ok(dateAfterSecond);
        Assert.Equal(dateCountAfterFirst, dateAfterSecond.Value.Count);
        Assert.Equal(folderAfterFirst.FolderVersion, second.Value.Folder!.FolderVersion);
    }

    [Fact]
    public void AddEmail_treats_identical_bytes_as_one_identity_but_a_one_byte_change_as_distinct()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Hash", "the raw body bytes");
        byte[] identical = (byte[])mime.Clone();
        byte[] oneByteOff = (byte[])mime.Clone();
        oneByteOff[^1] ^= 0x01; // flip a single bit of the final byte

        // EmailHashedID is a pure content hash: byte-identical content collapses to one identity,
        // while a single differing byte produces a distinct EmailHashedID.
        Assert.Equal(
            EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(mime),
            EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(identical));
        Assert.NotEqual(
            EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(mime),
            EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(oneByteOff));

        var first = mgr.AddEmail(Request(folder, mime, 1000));
        Ok(first);
        Assert.False(first.Value.IsDuplicate);

        // Byte-identical re-add → duplicate: same identity, no new content block stored.
        var dup = mgr.AddEmail(Request(first.Value.Folder!, identical, 2000));
        Ok(dup);
        Assert.True(dup.Value.IsDuplicate);
        Assert.Equal(first.Value.EmailId, dup.Value.EmailId);
        Assert.Null(dup.Value.ContentBlockId);

        // One byte different → NOT a duplicate: a distinct identity is stored in its own block.
        var distinct = mgr.AddEmail(Request(dup.Value.Folder!, oneByteOff, 3000));
        Ok(distinct);
        Assert.False(distinct.Value.IsDuplicate);
        Assert.NotEqual(first.Value.EmailId, distinct.Value.EmailId);
        Assert.NotNull(distinct.Value.ContentBlockId);
        Assert.NotEqual(first.Value.ContentBlockId, distinct.Value.ContentBlockId);

        // Exactly two genuine emails were buffered; the duplicate contributed nothing.
        Assert.Equal(2, mgr.PrimaryIndex!.PendingCount);
    }

    // --------------------------------------------------------- WAL

    [Fact]
    public void AddEmail_logs_a_wal_entry_and_advances_the_wal_sequence()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        Assert.Null(mgr.Wal!.LastSequence); // nothing logged yet

        var added = mgr.AddEmail(Request(NewFolder(), Mime("WAL", "body"), 500));
        Ok(added);

        Assert.NotNull(added.Value.WalSequence);
        Assert.True(mgr.Wal.HasWritten);
        Assert.Equal(added.Value.WalSequence, mgr.Wal.LastSequence);
        Assert.Equal(WalWriter.InitialSequence, added.Value.WalSequence);
    }

    // --------------------------------------------------------- Folder delta

    [Fact]
    public void AddEmail_appends_a_readable_folder_delta_and_advances_the_directory()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Folder", "body for folder delta");
        long ticks = 12345;

        var added = mgr.AddEmail(Request(folder, mime, ticks));
        Ok(added);
        var r = added.Value;

        // Directory advanced: FolderVersion incremented and its head points at the new delta block.
        Assert.NotNull(r.DeltaBlockId);
        Assert.NotNull(r.Folder);
        Assert.Equal(folder.FolderVersion + 1, r.Folder!.FolderVersion);
        Assert.True(r.Folder.HasPendingDelta);
        Assert.Equal(r.DeltaBlockId, r.Folder.HeadDeltaBlockId);

        // The appended delta block is a readable Add carrying the email's listing row.
        var delta = mgr.FolderDeltas!.ReadDeltaBlock(OffsetOf(mgr, r.DeltaBlockId!));
        Ok(delta);
        var entry = Assert.Single(delta.Value.Entries);
        Assert.Equal(FolderDeltaOp.Add, entry.Op);
        Assert.Equal(r.EmailId, entry.EmailHashedId);
        Assert.NotNull(entry.Record);
        Assert.Equal(r.ContentBlockId, entry.Record!.ContentBlockId);
        Assert.Equal(ticks, entry.Record.DateTicks);
        Assert.Equal(mime.Length, entry.Record.MessageSize);
        Assert.Equal("probe", entry.Record.Subject);
    }

    // --------------------------------------------------------- Encrypted file

    [Fact]
    public void AddEmail_on_an_encrypted_file_stores_encrypted_tier_blocks()
    {
        var create = EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = "correct horse battery staple",
            KdfParameters = FastParams,
        });
        Ok(create);
        create.Value.Dispose();

        using var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions
        {
            Password = "correct horse battery staple",
        }).Value;
        Assert.True(mgr.IsEncrypted);

        byte[] mime = Mime("Secret", "sensitive body bytes");
        var added = mgr.AddEmail(Request(NewFolder(), mime, 777));
        Ok(added);
        var r = added.Value;

        // On disk the Tier 3 block is AES-256-GCM ciphertext, not the plaintext MIME.
        var raw = mgr.BlockManager.Read(OffsetOf(mgr, r.ContentBlockId!));
        Ok(raw);
        Assert.True(raw.Value.Header.IsEncrypted);
        Assert.NotEqual(mime, raw.Value.Payload);

        // Decrypting under the header's epoch recovers the exact raw MIME.
        var plaintext = mgr.EncryptionProvider!.Decrypt(
            raw.Value.Payload, raw.Value.Header.BlockId, raw.Value.Header.Type, raw.Value.Header.KeyEpoch);
        Assert.Equal(mime, plaintext);

        // The Tier 2 metadata block is likewise encrypted.
        var meta = mgr.BlockManager.Read(OffsetOf(mgr, r.MetadataBlockId!));
        Ok(meta);
        Assert.Equal(BlockType.EmailMetadata, meta.Value.Header.Type);
        Assert.True(meta.Value.Header.IsEncrypted);
    }

    // --------------------------------------------------------- Guards

    [Fact]
    public void AddEmail_on_a_created_but_unopened_manager_fails()
    {
        using var created = EmailManager.Create(_path).Value; // Create, never Open
        var result = created.AddEmail(Request(NewFolder(), Mime("x", "y"), 1));
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void AddEmail_rejects_a_negative_date()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var result = mgr.AddEmail(Request(NewFolder(), Mime("neg", "body"), -1));
        Assert.True(result.IsFailure);
    }
}
