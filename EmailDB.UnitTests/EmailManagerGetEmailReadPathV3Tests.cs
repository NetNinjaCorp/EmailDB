using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 open-email read path (US-EMDB-86-5): <see cref="EmailManager.GetEmail"/> (Tier 3
/// raw MIME) and <see cref="EmailManager.GetMetadata"/> (Tier 2 metadata, no Tier 3 read). Covers the
/// story's acceptance criteria: content retrieval for any committed email in O(log n) block reads,
/// metadata-only fetch that reads Tier 2 without touching Tier 3, a clean not-found for an unknown
/// identity (never an exception), and checksum verification on the read path (EmailDB_FileFormat_Spec.md
/// Sections 4, 6-7, 11.1, docs/BTree_Index.md Section 5).
/// </summary>
public class EmailManagerGetEmailReadPathV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-getemail-{Guid.NewGuid():N}.emdb");

    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(string subject, string body) =>
        Encoding.UTF8.GetBytes(
            $"From: sender@example.com\r\nTo: rcpt@example.com\r\nSubject: {subject}\r\n\r\n{body}");

    private static FolderPageDirectory NewFolder() =>
        FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

    private static AddEmailRequest Request(FolderPageDirectory folder, byte[] mime, byte[] metadata, long ticks) => new()
    {
        RawContent = mime,
        Folder = folder,
        MetadataPayload = metadata,
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = "sender@example.com",
        Subject = "probe",
        Preview = "preview body text",
    };

    // ---- GetEmail: content for any committed email (AC1) ----------------------------------------

    [Fact]
    public void GetEmail_CommittedEmail_ReturnsRawContent()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        byte[] mime = Mime("hello", "the body");
        var added = mgr.AddEmail(Request(NewFolder(), mime, Encoding.UTF8.GetBytes("meta"), 100));
        Ok(added);
        Ok(mgr.Commit());

        var got = mgr.GetEmail(added.Value.EmailId);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(mime, got.Value.Content);
    }

    [Fact]
    public void GetEmail_AfterReopen_ReturnsRawContent()
    {
        V3Id id;
        byte[] mime = Mime("persist", "reopen body");

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, Encoding.UTF8.GetBytes("m"), 7));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path).Value;
        var got = reopened.GetEmail(id);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(mime, got.Value.Content);
    }

    [Fact]
    public void GetEmail_ManyEmails_EachRetrievable()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();

        var expected = new List<(V3Id Id, byte[] Mime)>();
        for (int i = 0; i < 200; i++)
        {
            byte[] mime = Mime($"s{i}", $"body number {i} lorem ipsum");
            var added = mgr.AddEmail(Request(folder, mime, Encoding.UTF8.GetBytes($"meta-{i}"), i + 1));
            Ok(added);
            folder = added.Value.Folder!;
            expected.Add((added.Value.EmailId, mime));
        }
        Ok(mgr.Commit());

        foreach (var (eid, mime) in expected)
        {
            var got = mgr.GetEmail(eid);
            Ok(got);
            Assert.True(got.Value.Found);
            Assert.Equal(mime, got.Value.Content);
        }
    }

    [Fact]
    public void GetEmail_EncryptedFile_DecryptsContent()
    {
        var opts = new EmailManagerCreateOptions { Password = "correct horse", KdfParameters = FastParams };
        EmailManager.Create(_path, opts).Value.Dispose();
        using var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = "correct horse" }).Value;

        byte[] mime = Mime("secret", "ciphertext on disk");
        var added = mgr.AddEmail(Request(NewFolder(), mime, Encoding.UTF8.GetBytes("secret-meta"), 42));
        Ok(added);
        Ok(mgr.Commit());

        var got = mgr.GetEmail(added.Value.EmailId);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(mime, got.Value.Content);
    }

    [Fact]
    public void GetEmail_BlockReads_ScaleWithTreeHeight_NotEmailCount()
    {
        // O(log n) evidence (story AC1). A committed email's GetEmail reads a number of blocks that
        // tracks the index HEIGHT (log n) plus a constant for the content block — NOT the email count.
        // Grow the mailbox 16x; the per-GetEmail cold-cache block-read count must stay essentially flat
        // (grow only by log-depth), never ~16x as a per-email page scan would. BlockManager.ReadCount
        // counts every physical block read on the whole path (primary-index nodes -> location-index
        // nodes -> content block), and a fresh Open gives an empty node cache, so snapshotting the
        // counter immediately before a single GetEmail isolates exactly that lookup's cold reads.
        long ColdReadsForMailbox(int n)
        {
            var path = Path.Combine(Path.GetTempPath(), $"emaildb-getemail-scale-{Guid.NewGuid():N}.emdb");
            try
            {
                V3Id probe;
                EmailManager.Create(path).Value.Dispose();
                using (var mgr = EmailManager.Open(path).Value)
                {
                    var folder = NewFolder();
                    V3Id? mid = null;
                    for (int i = 0; i < n; i++)
                    {
                        var added = mgr.AddEmail(Request(folder, Mime($"s{i}", $"body number {i}"), Encoding.UTF8.GetBytes($"m{i}"), i + 1));
                        Ok(added);
                        folder = added.Value.Folder!;
                        if (i == n / 2) mid = added.Value.EmailId; // a key buried in the middle of the tree
                    }
                    Ok(mgr.Commit());
                    probe = mid!.Value;
                    Ok(mgr.Close());
                }

                // Cold cache: a fresh Open rebuilds the index with an empty node cache. Snapshot the
                // read counter AFTER Open so only the single GetEmail's physical reads are measured.
                using var reopened = EmailManager.Open(path).Value;
                long before = reopened.BlockManager.ReadCount;
                var got = reopened.GetEmail(probe);
                Ok(got);
                Assert.True(got.Value.Found);
                return reopened.BlockManager.ReadCount - before;
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        long smallReads = ColdReadsForMailbox(64);
        long bigReads = ColdReadsForMailbox(1024); // 16x the emails

        // A real lookup touches at least the content block plus index nodes.
        Assert.True(smallReads >= 1, $"expected a lookup to read >= 1 block, read {smallReads}");
        // A per-email scan would read ~16x more blocks for 16x the mail. Instead reads grow only by
        // index depth (a couple of blocks per added level). Budget = a small log-depth constant; a
        // proportional O(n) path (~16x = ~{smallReads * 16}) blows straight past it.
        const long logDepthBudget = 6;
        Assert.True(bigReads <= smallReads + logDepthBudget,
            $"cold-cache GetEmail read {smallReads} blocks in a 64-email mailbox and {bigReads} in a 1024-email (16x) mailbox; " +
            $"O(log n) allows growth <= {logDepthBudget} (log-depth only), but a per-email scan would read ~16x more (~{smallReads * 16}).");
    }

    // ---- GetMetadata: Tier 2 without touching Tier 3 (AC2) --------------------------------------

    [Fact]
    public void GetMetadata_CommittedEmail_ReturnsTier2Payload()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        byte[] mime = Mime("m", "the big body");
        byte[] meta = Encoding.UTF8.GetBytes("tier-2-headers-and-preview");
        var added = mgr.AddEmail(Request(NewFolder(), mime, meta, 5));
        Ok(added);
        Ok(mgr.Commit());

        var got = mgr.GetMetadata(added.Value.EmailId);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(meta, got.Value.Content);
        // Distinct from Tier 3 content: metadata-only fetch returned Tier 2, not the raw MIME.
        Assert.NotEqual(mime, got.Value.Content);
    }

    [Fact]
    public void GetMetadata_AfterReopen_ReturnsTier2Payload()
    {
        V3Id id;
        byte[] meta = Encoding.UTF8.GetBytes("persisted-tier2");

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("s", "b"), meta, 9));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path).Value;
        var got = reopened.GetMetadata(id);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(meta, got.Value.Content);
    }

    [Fact]
    public void GetMetadata_EncryptedFile_DecryptsTier2()
    {
        var opts = new EmailManagerCreateOptions { Password = "pw", KdfParameters = FastParams };
        EmailManager.Create(_path, opts).Value.Dispose();
        using var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = "pw" }).Value;

        byte[] meta = Encoding.UTF8.GetBytes("encrypted-tier2-metadata");
        var added = mgr.AddEmail(Request(NewFolder(), Mime("s", "b"), meta, 3));
        Ok(added);
        Ok(mgr.Commit());

        var got = mgr.GetMetadata(added.Value.EmailId);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(meta, got.Value.Content);
    }

    [Fact]
    public void GetMetadata_ResolvesToEmailMetadataBlock()
    {
        // The metadata offset the read path computes must land on the Tier 2 EmailMetadata block —
        // proven by reading it back at that offset and asserting the block type, without reading Tier 3.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var added = mgr.AddEmail(Request(NewFolder(), Mime("s", "b"), Encoding.UTF8.GetBytes("m2"), 11));
        Ok(added);
        Ok(mgr.Commit());

        Assert.True(mgr.Resolver!.TryGetLocation(added.Value.ContentBlockId!, out var contentLoc) && contentLoc is not null);
        long metadataOffset = contentLoc!.Offset + contentLoc.TotalBlockLength;
        var block = mgr.BlockManager.Read(metadataOffset);
        Ok(block);
        Assert.Equal(BlockType.EmailMetadata, block.Value.Header.Type);
        Assert.Equal(added.Value.MetadataBlockId, block.Value.Header.BlockId);
    }

    // True iff the half-open file ranges [aStart, aEnd) and [bStart, bEnd) share any byte.
    private static bool Intersects(long aStart, long aEnd, long bStart, long bEnd) =>
        aStart < bEnd && bStart < aEnd;

    [Theory]
    [InlineData(null)]      // plaintext file
    [InlineData("pw-byte")] // encrypted file: the same guarantee must hold over ciphertext
    public void GetMetadata_NeverReadsAnyByteOfTheTier3ContentBlock(string? password)
    {
        // Byte-range positional proof of the acceptance criterion: a metadata-only fetch reads Tier 2
        // WITHOUT touching Tier 3. BlockManager.ReadRangeLog records the exact [offset,length) file
        // range of every verified block read on the path. We compute the Tier 3 content block's whole
        // on-disk byte range from the location index, then assert that NOT ONE recorded read range
        // (index nodes -> metadata block) intersects it — GetMetadata never reads even the content
        // block's header, let alone its payload bytes.
        var createOpts = password is null ? null : new EmailManagerCreateOptions { Password = password, KdfParameters = FastParams };
        var openOpts = password is null ? null : new EmailManagerOpenOptions { Password = password };

        V3Id id;
        byte[] contentBlockId;
        byte[] meta = Encoding.UTF8.GetBytes("tier-2-only");
        // A large body makes the Tier 3 content block a wide byte range; if any read strayed into it
        // (payload or header) the intersection assert would catch it.
        byte[] mime = Mime("big", new string('B', 40_000));

        EmailManager.Create(_path, createOpts).Value.Dispose();
        using (var mgr = EmailManager.Open(_path, openOpts).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, meta, 5));
            Ok(added);
            id = added.Value.EmailId;
            contentBlockId = added.Value.ContentBlockId!;
            Ok(mgr.Close());
        }

        // Cold reopen so the whole lookup (index nodes + the block read) goes through the stream and is
        // captured by the range log — nothing is served from a warm in-memory node cache.
        using var reopened = EmailManager.Open(_path, openOpts).Value;

        // The Tier 3 content block's full on-disk range [contentStart, contentEnd), from the location
        // index only — we read the block's location, never its bytes.
        Assert.True(reopened.Resolver!.TryGetLocation(contentBlockId, out var contentLoc) && contentLoc is not null);
        long contentStart = contentLoc!.Offset;
        long contentEnd = contentLoc.Offset + contentLoc.TotalBlockLength;

        // Record every byte range GetMetadata reads, then run exactly one metadata fetch.
        var log = new List<(long Offset, long Length)>();
        reopened.BlockManager.ReadRangeLog = log;
        var got = reopened.GetMetadata(id);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(meta, got.Value.Content);

        Assert.NotEmpty(log); // it really did read something (at minimum the Tier 2 block)
        foreach (var (off, len) in log)
            Assert.False(Intersects(off, off + len, contentStart, contentEnd),
                $"GetMetadata read file range [{off},{off + len}) which intersects the Tier 3 content block " +
                $"range [{contentStart},{contentEnd}); metadata-only fetch must never touch Tier 3.");

        // Positive control: the Tier 2 metadata block (immediately after Tier 3) WAS among the reads.
        Assert.Contains(log, r => r.Offset == contentEnd);
    }

    [Fact]
    public void GetMetadata_WithCorruptedTier3Payload_StillSucceeds_WhileGetEmailFails()
    {
        // Behavioral proof that Tier 3 is untouched: deliberately corrupt the Tier 3 content payload on
        // disk. Every block read on the path is checksum-verified, so if GetMetadata read the content
        // payload it would fail verification. Instead GetMetadata still returns the correct Tier 2 bytes
        // (it never read Tier 3), while GetEmail on the same file DOES fail the checksum — the exact
        // contrast that pins the read to Tier 2 only.
        V3Id id;
        long contentOffset;
        byte[] meta = Encoding.UTF8.GetBytes("survives-tier3-corruption");

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("s", "corrupt my body"), meta, 2));
            Ok(added);
            id = added.Value.EmailId;
            Assert.True(mgr.Resolver!.TryGetLocation(added.Value.ContentBlockId!, out var loc) && loc is not null);
            contentOffset = loc!.Offset;
            Ok(mgr.Close());
        }

        // Flip a byte inside the Tier 3 content block's payload region (past the 64-byte header+checksums).
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            long payloadByte = contentOffset + 64 + 1;
            fs.Seek(payloadByte, SeekOrigin.Begin);
            int b = fs.ReadByte();
            fs.Seek(payloadByte, SeekOrigin.Begin);
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        using var reopened = EmailManager.Open(_path).Value;

        // Metadata-only fetch is unaffected by the Tier 3 corruption: it never read those bytes.
        var meta2 = reopened.GetMetadata(id);
        Ok(meta2);
        Assert.True(meta2.Value.Found);
        Assert.Equal(meta, meta2.Value.Content);

        // The full read DOES read Tier 3 and must reject the corrupted payload — proving the bytes
        // GetMetadata skipped are exactly the ones a body read verifies.
        var full = reopened.GetEmail(id);
        Assert.True(full.IsFailure, "GetEmail must fail the checksum on the corrupted Tier 3 payload");
    }

    [Fact]
    public void GetMetadata_ReadsFewerBytesThanGetEmail_ForTheSameEmail()
    {
        // "Cheaper than a full fetch": with a large Tier 3 body and a small Tier 2 payload, both fetches
        // traverse the identical index path and read one email-tier block, but GetMetadata's block is the
        // small Tier 2 one — so it reads strictly fewer bytes off disk. Byte totals come from the
        // ReadRangeLog; each fetch runs on its own cold Open so index-node reads are counted identically.
        V3Id id;
        byte[] meta = Encoding.UTF8.GetBytes("tiny");                 // small Tier 2
        byte[] mime = Mime("big", new string('X', 60_000));           // large Tier 3

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, meta, 1));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close());
        }

        (long bytes, long blocks) Measure(Func<EmailManager, Result<EmailReadResult>> fetch)
        {
            using var m = EmailManager.Open(_path).Value;
            var log = new List<(long Offset, long Length)>();
            m.BlockManager.ReadRangeLog = log;
            long beforeBlocks = m.BlockManager.ReadCount;
            var r = fetch(m);
            Ok(r);
            Assert.True(r.Value.Found);
            return (log.Sum(e => e.Length), m.BlockManager.ReadCount - beforeBlocks);
        }

        var metaCost = Measure(m => m.GetMetadata(id));
        var emailCost = Measure(m => m.GetEmail(id));

        Assert.True(metaCost.bytes < emailCost.bytes,
            $"metadata-only fetch read {metaCost.bytes} bytes but the full fetch read {emailCost.bytes}; " +
            "opening an email without its body must be cheaper.");
        // Same number of block reads (identical index path + one email block); the win is in bytes, and
        // the metadata fetch is never more blocks than the full fetch.
        Assert.True(metaCost.blocks <= emailCost.blocks,
            $"metadata fetch read {metaCost.blocks} blocks vs {emailCost.blocks} for the full fetch.");
    }

    // ---- Unknown ID: clean not-found (AC3) ------------------------------------------------------

    [Fact]
    public void GetEmail_UnknownId_EmptyIndex_CleanNotFound()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var got = mgr.GetEmail(V3Id.ComputeFromRawContent(Mime("never", "added")));
        Ok(got); // a success, not a failure/exception
        Assert.False(got.Value.Found);
        Assert.Null(got.Value.Content);
    }

    [Fact]
    public void GetEmail_UnknownId_NonEmptyIndex_CleanNotFound()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        Ok(mgr.AddEmail(Request(NewFolder(), Mime("present", "x"), Encoding.UTF8.GetBytes("m"), 1)));
        Ok(mgr.Commit());

        var got = mgr.GetEmail(V3Id.ComputeFromRawContent(Mime("absent", "y")));
        Ok(got);
        Assert.False(got.Value.Found);
    }

    [Fact]
    public void GetMetadata_UnknownId_CleanNotFound()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        Ok(mgr.AddEmail(Request(NewFolder(), Mime("present", "x"), Encoding.UTF8.GetBytes("m"), 1)));
        Ok(mgr.Commit());

        var got = mgr.GetMetadata(V3Id.ComputeFromRawContent(Mime("absent", "y")));
        Ok(got);
        Assert.False(got.Value.Found);
        Assert.Null(got.Value.Content);
    }

    [Fact]
    public void GetMetadata_UnknownId_EmptyIndex_CleanNotFound()
    {
        // Symmetry with GetEmail_UnknownId_EmptyIndex: the metadata-only path must also give a clean
        // not-found (never an exception) when the index holds nothing at all.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var got = mgr.GetMetadata(V3Id.ComputeFromRawContent(Mime("never", "added")));
        Ok(got); // a success, not a failure/exception
        Assert.False(got.Value.Found);
        Assert.Null(got.Value.Content);
    }

    [Fact]
    public void GetEmailAndMetadata_UnknownId_AfterReopen_CleanNotFound()
    {
        // A committed, then reopened mailbox rebuilds the index from the persisted IndexRoot. An unknown
        // ID on that rebuilt index must still be a clean not-found on BOTH read entry points.
        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            Ok(mgr.AddEmail(Request(NewFolder(), Mime("present", "x"), Encoding.UTF8.GetBytes("m"), 1)));
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path).Value;
        var unknown = V3Id.ComputeFromRawContent(Mime("absent", "y"));

        var email = reopened.GetEmail(unknown);
        Ok(email);
        Assert.False(email.Value.Found);
        Assert.Null(email.Value.Content);

        var meta = reopened.GetMetadata(unknown);
        Ok(meta);
        Assert.False(meta.Value.Found);
        Assert.Null(meta.Value.Content);
    }

    [Fact]
    public void GetEmailAndMetadata_UnknownId_EncryptedFile_CleanNotFound()
    {
        // The not-found short-circuits at the index lookup, before any block read/decrypt — so it must
        // never depend on or trip over encryption. Prove it returns a clean not-found on an encrypted
        // file (which does hold a real, encrypted email) for both entry points.
        var opts = new EmailManagerCreateOptions { Password = "pw", KdfParameters = FastParams };
        EmailManager.Create(_path, opts).Value.Dispose();
        using var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = "pw" }).Value;
        Ok(mgr.AddEmail(Request(NewFolder(), Mime("present", "x"), Encoding.UTF8.GetBytes("m"), 1)));
        Ok(mgr.Commit());

        var unknown = V3Id.ComputeFromRawContent(Mime("absent", "y"));

        var email = mgr.GetEmail(unknown);
        Ok(email);
        Assert.False(email.Value.Found);
        Assert.Null(email.Value.Content);

        var meta = mgr.GetMetadata(unknown);
        Ok(meta);
        Assert.False(meta.Value.Found);
        Assert.Null(meta.Value.Content);
    }

    [Theory]
    [InlineData(0x00)] // the all-zero identity (EmailHashedID.Empty sentinel) — a valid 32-byte key
    [InlineData(0xFF)] // the all-ones identity — the lexicographic maximum key
    public void GetEmailAndMetadata_BoundaryId_CleanNotFound(int fill)
    {
        // Boundary keys at the extremes of the 32-byte key space must be looked up like any other and
        // yield a clean not-found — no sentinel/edge-case throws. Exercised against a populated index so
        // the traversal actually descends past the root.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();
        for (int i = 0; i < 32; i++)
        {
            var added = mgr.AddEmail(Request(folder, Mime($"s{i}", $"b{i}"), Encoding.UTF8.GetBytes($"m{i}"), i + 1));
            Ok(added);
            folder = added.Value.Folder!;
        }
        Ok(mgr.Commit());

        var boundary = new byte[V3Id.Size];
        Array.Fill(boundary, (byte)fill);
        var id = new V3Id(boundary);

        var email = mgr.GetEmail(id);
        Ok(email);
        Assert.False(email.Value.Found);

        var meta = mgr.GetMetadata(id);
        Ok(meta);
        Assert.False(meta.Value.Found);
    }

    [Fact]
    public void GetEmailAndMetadata_NearMissId_OneBitOffFromRealKey_CleanNotFound()
    {
        // The strongest near-miss: a well-formed key that differs from a REAL committed key by a single
        // bit. It must miss cleanly (not collide, not throw) on both entry points — proving the lookup
        // compares the full key, not a truncated/loose prefix.
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;
        var folder = NewFolder();
        V3Id real = default;
        for (int i = 0; i < 32; i++)
        {
            var added = mgr.AddEmail(Request(folder, Mime($"s{i}", $"b{i}"), Encoding.UTF8.GetBytes($"m{i}"), i + 1));
            Ok(added);
            folder = added.Value.Folder!;
            if (i == 16) real = added.Value.EmailId;
        }
        Ok(mgr.Commit());

        // Sanity: the real key is genuinely present, so a miss on the neighbour is meaningful.
        Assert.True(mgr.GetEmail(real).Value.Found);

        byte[] neighbour = real.GetBytes();
        neighbour[^1] ^= 0x01; // flip the least-significant bit of the last byte
        var near = new V3Id(neighbour);
        Assert.NotEqual(real, near);

        var email = mgr.GetEmail(near);
        Ok(email);
        Assert.False(email.Value.Found);

        var meta = mgr.GetMetadata(near);
        Ok(meta);
        Assert.False(meta.Value.Found);
    }

    [Theory]
    [InlineData(0)]  // empty span
    [InlineData(16)] // too short
    [InlineData(31)] // one byte short
    [InlineData(33)] // one byte long
    [InlineData(64)] // too long
    public void EmailHashedID_MalformedLengthInput_ThrowsCleanArgumentException(int length)
    {
        // The API contract for malformed input: an EmailHashedID is exactly 32 bytes, and a wrong-length
        // span is rejected at construction with a clean ArgumentException — never a corrupt key that
        // silently reaches GetEmail. This is the API's guard that keeps GetEmail/GetMetadata always
        // receiving a well-formed 32-byte identity, so their not-found path can never see garbage.
        var bytes = new byte[length];
        var ex = Assert.Throws<ArgumentException>(() => new V3Id(bytes));
        Assert.Equal("digest", ex.ParamName);
    }

    [Fact]
    public void GetEmailAndMetadata_UnknownId_ReadNoEmailTierBlock_BeyondIndexDescent()
    {
        // "No unnecessary block reads beyond the index descent": a not-found stops at the primary-index
        // lookup and reads ZERO email-tier blocks. The primary index is held in memory after Open, so
        // its descent hits no disk block; the ONLY physical BlockManager read on a hit is the email-tier
        // block (EmailContent / EmailMetadata). We therefore measure the physical read-count delta:
        //   * a real fetch reads >= 1 block (positive control — the path can and does read),
        //   * an unknown-ID fetch reads EXACTLY 0 (it never reaches ReadEmailBlockPayload),
        // and, belt-and-suspenders, the not-found range log intersects no committed email block's bytes.
        var emailRanges = new List<(long Start, long End)>();
        V3Id realId = default;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folder = NewFolder();
            var emailBlockIds = new List<byte[]>();
            for (int i = 0; i < 40; i++)
            {
                // Sizeable bodies so each content block is a wide, unmistakable byte range.
                var added = mgr.AddEmail(Request(folder, Mime($"s{i}", new string('B', 2_000)), Encoding.UTF8.GetBytes($"metadata-{i}"), i + 1));
                Ok(added);
                folder = added.Value.Folder!;
                if (i == 20) realId = added.Value.EmailId;
                emailBlockIds.Add(added.Value.ContentBlockId!);  // Tier 3 EmailContent
                emailBlockIds.Add(added.Value.MetadataBlockId!); // Tier 2 EmailMetadata
            }
            Ok(mgr.Commit());

            // Collect the full on-disk byte range of every committed email-tier block (both tiers), from
            // the location index only — we read each block's location, never its bytes.
            foreach (var bid in emailBlockIds)
            {
                Assert.True(mgr.Resolver!.TryGetLocation(bid, out var loc) && loc is not null);
                emailRanges.Add((loc!.Offset, loc.Offset + loc.TotalBlockLength));
            }
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path).Value;
        var unknown = V3Id.ComputeFromRawContent(Mime("absent", "never committed"));

        long ReadDelta(Func<EmailManager, Result<EmailReadResult>> fetch, out Result<EmailReadResult> r, List<(long Offset, long Length)>? log = null)
        {
            if (log is not null) reopened.BlockManager.ReadRangeLog = log;
            long before = reopened.BlockManager.ReadCount;
            r = fetch(reopened);
            reopened.BlockManager.ReadRangeLog = null;
            return reopened.BlockManager.ReadCount - before;
        }

        // Positive control: a real fetch DOES read at least one email-tier block.
        long realEmailReads = ReadDelta(m => m.GetEmail(realId), out var realEmail);
        Ok(realEmail);
        Assert.True(realEmail.Value.Found);
        Assert.True(realEmailReads >= 1, $"a real GetEmail should read >= 1 block, read {realEmailReads}");

        // Unknown ID: zero physical block reads on BOTH entry points — nothing beyond the in-memory descent.
        var emailLog = new List<(long Offset, long Length)>();
        long unknownEmailReads = ReadDelta(m => m.GetEmail(unknown), out var unknownEmail, emailLog);
        Ok(unknownEmail);
        Assert.False(unknownEmail.Value.Found);
        Assert.Equal(0, unknownEmailReads);

        var metaLog = new List<(long Offset, long Length)>();
        long unknownMetaReads = ReadDelta(m => m.GetMetadata(unknown), out var unknownMeta, metaLog);
        Ok(unknownMeta);
        Assert.False(unknownMeta.Value.Found);
        Assert.Equal(0, unknownMetaReads);

        // Belt-and-suspenders: no recorded read of either not-found lookup touches any committed email block.
        foreach (var (off, len) in emailLog.Concat(metaLog))
            foreach (var (start, end) in emailRanges)
                Assert.False(Intersects(off, off + len, start, end),
                    $"a not-found lookup read file range [{off},{off + len}) intersecting an email-tier block " +
                    $"[{start},{end}); a clean not-found must read no EmailContent/EmailMetadata block.");
    }

    // ---- Read path verifies checksums / Merkle path (AC4) ---------------------------------------

    [Fact]
    public void GetEmail_CorruptedContentPayload_FailsChecksumVerification()
    {
        V3Id id;
        long contentOffset;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("s", "verify me"), Encoding.UTF8.GetBytes("m"), 2));
            Ok(added);
            id = added.Value.EmailId;
            Assert.True(mgr.Resolver!.TryGetLocation(added.Value.ContentBlockId!, out var loc) && loc is not null);
            contentOffset = loc!.Offset;
            Ok(mgr.Close());
        }

        // Flip a byte inside the content block's payload region (after the 64-byte header+checksums).
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            long payloadByte = contentOffset + 64 + 1;
            fs.Seek(payloadByte, SeekOrigin.Begin);
            int b = fs.ReadByte();
            fs.Seek(payloadByte, SeekOrigin.Begin);
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        using var reopened = EmailManager.Open(_path).Value;
        var got = reopened.GetEmail(id);
        // The lookup found the key, but the verified block read must reject the corrupted payload.
        Assert.True(got.IsFailure, "corrupted content payload must fail the checksum-verified read");
    }

    // Re-lays the block at <paramref name="offset"/> on disk with one payload byte flipped, but
    // RECOMPUTES the block's header+payload checksums so the block itself stays perfectly valid. The
    // tamper is therefore invisible to the checksum layer — only a HIGHER integrity check (the B+-tree
    // Merkle root-hash anchoring, or AES-GCM authentication) can catch it. This is what isolates
    // "verifies the Merkle path" / "GCM auth" from mere block-checksum verification.
    private void TamperBlockPayloadKeepingChecksumValid(long offset, int payloadByteIndex)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var mgr = new BlockManager(fs, ownsStream: false);
        var read = mgr.Read(offset);
        Assert.True(read.IsSuccess, read.IsFailure ? read.Error : null);
        byte[] payload = (byte[])read.Value.Payload.Clone();
        payload[payloadByteIndex] ^= 0xFF;
        byte[] reserialized = BlockSerializer.Serialize(read.Value.Header, payload);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(reserialized, 0, reserialized.Length);
        fs.Flush();
    }

    // Flips a single raw byte on disk WITHOUT fixing the checksum — the block's own checksum layer
    // must catch it. Used to corrupt a header field or a payload region and prove the checksum-verified
    // read rejects it.
    private void FlipRawByteOnDisk(long byteOffset)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(byteOffset, SeekOrigin.Begin);
        int b = fs.ReadByte();
        fs.Seek(byteOffset, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    [Fact]
    public void GetEmail_CorruptedContentBlockHeader_FailsHeaderChecksumVerification()
    {
        // The read path verifies the HEADER checksum before trusting any header field (spec Section 4).
        // Flip a byte inside the content block's 48-byte header region (the 16-byte BlockId at header
        // offset 16), leaving the header checksum stale: the verified read must reject it as a clean
        // failure, never read the block under a corrupt header.
        V3Id id;
        long contentOffset;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("s", "header verify"), Encoding.UTF8.GetBytes("m"), 2));
            Ok(added);
            id = added.Value.EmailId;
            Assert.True(mgr.Resolver!.TryGetLocation(added.Value.ContentBlockId!, out var loc) && loc is not null);
            contentOffset = loc!.Offset;
            Ok(mgr.Close());
        }

        // Corrupt a header field (BlockId lives at header offset 16), before the header checksum at
        // offset 48 — so the stale header checksum catches it.
        FlipRawByteOnDisk(contentOffset + 16);

        using var reopened = EmailManager.Open(_path).Value;
        var got = reopened.GetEmail(id);
        Assert.True(got.IsFailure, "a corrupted content block header must fail the header-checksum-verified read");
        Assert.False(string.IsNullOrWhiteSpace(got.Error));
    }

    [Fact]
    public void GetEmail_CorruptedPrimaryIndexRootNodeOnDisk_FailsVerification_NeverReturnsWrongData()
    {
        // Corrupt an interior B+-tree node (the primary-index root) ON DISK after commit, then reopen
        // COLD so the node cache is empty and the descent must read the tampered node from the stream.
        // Every GetEmail descends through the root, so its checksum-verified read must FAIL — never
        // return wrong data (spec Sections 6, 13, docs/BTree_Index.md Section 5).
        V3Id target = default;
        long rootNodeOffset;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folder = NewFolder();
            for (int i = 0; i < 64; i++)
            {
                var added = mgr.AddEmail(Request(folder, Mime($"s{i}", $"body {i}"), Encoding.UTF8.GetBytes($"m{i}"), i + 1));
                Ok(added);
                folder = added.Value.Folder!;
                if (i == 32) target = added.Value.EmailId;
            }
            Ok(mgr.Close());
        }

        // Resolve the primary-index root NODE block's on-disk offset from the reconstructed index.
        using (var probe = EmailManager.Open(_path).Value)
        {
            // Positive control: the target is genuinely retrievable before corruption.
            var pre = probe.GetEmail(target);
            Ok(pre);
            Assert.True(pre.Value.Found);

            var rootRef = probe.PrimaryIndex!.CommittedRoot!.RootRef;
            Assert.True(probe.Resolver!.TryGetLocation(rootRef.Reference, out var rootLoc) && rootLoc is not null);
            rootNodeOffset = rootLoc!.Offset;
            Ok(probe.Close());
        }

        // Flip a byte in the root node's payload region (past the 64-byte block header+checksums).
        FlipRawByteOnDisk(rootNodeOffset + 64 + 4);

        using var reopened = EmailManager.Open(_path).Value; // cold: empty node cache
        var got = reopened.GetEmail(target);
        Assert.True(got.IsFailure,
            "a corrupted B+-tree root node read cold from disk must fail verification, not return wrong data");
        Assert.False(string.IsNullOrWhiteSpace(got.Error));
    }

    [Fact]
    public void GetEmail_TamperedRootHashAnchor_FailsMerkleVerification_NeverReturnsWrongData()
    {
        // The read path anchors the tree's root against the RootHash stored in the committed IndexRoot
        // block (the Checkpoint's PrimaryIndexRoot). Tamper that stored RootHash while KEEPING the
        // IndexRoot block's checksum valid: Open still seeds the tree, but the first descent recomputes
        // the real root node's hash and finds it disagrees with the tampered anchor — a Merkle mismatch,
        // caught above the checksum layer. GetEmail must fail with the contracted Merkle error and never
        // return wrong data (spec Sections 6, 13).
        V3Id target = default;
        long indexRootOffset;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folder = NewFolder();
            for (int i = 0; i < 64; i++)
            {
                var added = mgr.AddEmail(Request(folder, Mime($"s{i}", $"body {i}"), Encoding.UTF8.GetBytes($"m{i}"), i + 1));
                Ok(added);
                folder = added.Value.Folder!;
                if (i == 32) target = added.Value.EmailId;
            }
            Ok(mgr.Close());
        }

        using (var probe = EmailManager.Open(_path).Value)
        {
            indexRootOffset = probe.Checkpoint!.PrimaryIndexRoot!.Offset;
            Ok(probe.Close());
        }

        // The IndexRoot descriptor payload is IndexKind(2)+RootBlockId(16)+EntryCount(8)+TreeHeight(2)+
        // RootHash(32)+Sequence(8); RootHash starts at payload offset 28. Flip a RootHash byte, keeping
        // the block's checksum valid so the tamper survives to the Merkle-anchoring check.
        TamperBlockPayloadKeepingChecksumValid(indexRootOffset, payloadByteIndex: 28);

        using var reopened = EmailManager.Open(_path).Value;
        var got = reopened.GetEmail(target);
        Assert.True(got.IsFailure,
            "a tampered root-hash anchor must fail Merkle verification on descent, not return wrong data");
        Assert.Contains(BTreeNodeStore.MerkleVerificationErrorCode, got.Error);
    }

    [Fact]
    public void GetEmail_EncryptedFile_TamperedCiphertext_FailsGcmAuthCleanly_NoException()
    {
        // Encrypted-file authenticity (spec Section 9.4, 13): flip a ciphertext byte in the Tier 3
        // content block while KEEPING the block checksum valid, so the checksum layer passes it through
        // and AES-GCM authentication is what must catch the tamper. The failure must surface CLEANLY as
        // a Result failure (never an unhandled exception, never wrong/garbage plaintext returned).
        var opts = new EmailManagerCreateOptions { Password = "correct horse", KdfParameters = FastParams };
        EmailManager.Create(_path, opts).Value.Dispose();

        V3Id id;
        long contentOffset, contentLength;
        byte[] mime = Mime("secret", "authenticated ciphertext");
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = "correct horse" }).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, Encoding.UTF8.GetBytes("secret-meta"), 42));
            Ok(added);
            id = added.Value.EmailId;
            Assert.True(mgr.Resolver!.TryGetLocation(added.Value.ContentBlockId!, out var loc) && loc is not null);
            contentOffset = loc!.Offset;
            contentLength = loc.TotalBlockLength;
            Ok(mgr.Close());
        }

        // Flip a byte in the middle of the ciphertext payload (nonce+ciphertext+tag) and re-checksum:
        // the block stays valid, so only GCM authentication can reject it. Payload byte index well
        // inside the ciphertext region (past the 12-byte nonce), safely within the payload length.
        Assert.True(contentLength > 64 + 40, "content block should be large enough to tamper mid-ciphertext");
        TamperBlockPayloadKeepingChecksumValid(contentOffset, payloadByteIndex: 20);

        using var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = "correct horse" }).Value;

        // Must be a CLEAN Result failure — the call must not throw.
        Result<EmailReadResult> got = default!;
        var ex = Record.Exception(() => got = reopened.GetEmail(id));
        Assert.Null(ex);
        Assert.True(got.IsFailure, "tampered ciphertext must fail GetEmail, not return wrong data");
        Assert.Contains("AES-GCM", got.Error);

        // And GetMetadata on the SAME file (its Tier 2 block is untampered) still works — the failure is
        // localized to the tampered block, not a wholesale open failure.
        var meta = reopened.GetMetadata(id);
        Ok(meta);
        Assert.True(meta.Value.Found);
    }

    [Fact]
    public void GetEmail_ValidEmail_MerkleVerifiedLookupSucceeds()
    {
        // A successful GetEmail exercises the Merkle-verified B+-tree traversal (each node checked
        // against its parent's ChildHash; root against IndexRoot.RootHash) end-to-end after reopen,
        // where the tree is rebuilt from the committed IndexRoot.
        V3Id id;
        byte[] mime = Mime("merkle", "verified traversal");

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            // Add several so the index has internal structure, not just a single leaf.
            var folder = NewFolder();
            V3Id? target = null;
            for (int i = 0; i < 64; i++)
            {
                var req = Request(folder, i == 0 ? mime : Mime($"s{i}", $"b{i}"), Encoding.UTF8.GetBytes($"m{i}"), i + 1);
                var added = mgr.AddEmail(req);
                Ok(added);
                folder = added.Value.Folder!;
                if (i == 0) target = added.Value.EmailId;
            }
            id = target!.Value;
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path).Value;
        var got = reopened.GetEmail(id);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(mime, got.Value.Content);
    }

    // ---- Guards ---------------------------------------------------------------------------------

    [Fact]
    public void GetEmail_OnCreatedButNotOpened_Fails()
    {
        using var created = EmailManager.Create(_path).Value; // created, never opened
        var got = created.GetEmail(V3Id.ComputeFromRawContent(Mime("s", "b")));
        Assert.True(got.IsFailure);
    }
}
