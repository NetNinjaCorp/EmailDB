using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// v3 port of the legacy scale end-to-end test (was RawBlockManager + offset
/// <c>BTreeIndex</c>). Drives the full v3 <see cref="EmailManager"/> pipeline
/// (EmailDB_FileFormat_Spec.md Sections 6-7, 11): add 1000 emails through the
/// group-commit AddEmail path, commit, then retrieve every email by its
/// content-addressed identity and verify the raw MIME round-trips — including
/// across a close/reopen cycle so the retrieval exercises the persisted index.
/// </summary>
public class EndToEndAdd1000EmailsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-e2e-1000-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(int i) =>
        Encoding.UTF8.GetBytes(
            $"From: sender{i}@e2e-test.com\r\nTo: recipient{i}@e2e-test.com\r\n" +
            $"Subject: E2E Subject {i}\r\n\r\nEmail body number {i} lorem ipsum dolor.");

    private static AddEmailRequest Request(FolderPageDirectory folder, int i) => new()
    {
        RawContent = Mime(i),
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes($"meta-{i}"),
        DateTicks = i + 1,
        Flags = ListingFlags.Read,
        From = $"sender{i}@e2e-test.com",
        Subject = $"E2E Subject {i}",
        Preview = $"Email body number {i}",
    };

    [Fact]
    public void Add1000Emails_ThenRetrieveEachById()
    {
        const int emailCount = 1000;
        var stored = new List<(V3Id Id, byte[] Mime)>(emailCount);

        EmailManager.Create(_path).Value.Dispose();

        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folder = FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

            // Phase 1: add 1000 emails through the v3 AddEmail pipeline.
            for (int i = 0; i < emailCount; i++)
            {
                var added = mgr.AddEmail(Request(folder, i));
                Ok(added);
                folder = added.Value.Folder!;
                stored.Add((added.Value.EmailId, Mime(i)));
            }

            Ok(mgr.Commit());

            // Phase 2 (in-process): retrieve each email by identity and verify the MIME.
            foreach (var (id, mime) in stored)
            {
                var got = mgr.GetEmail(id);
                Ok(got);
                Assert.True(got.Value.Found);
                Assert.Equal(mime, got.Value.Content);
            }

            Ok(mgr.Close());
        }

        // Phase 3 (after reopen): the persisted index resolves every identity too.
        using var reopened = EmailManager.Open(_path).Value;
        foreach (var (id, mime) in stored)
        {
            var got = reopened.GetEmail(id);
            Ok(got);
            Assert.True(got.Value.Found);
            Assert.Equal(mime, got.Value.Content);
        }
    }
}
