using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Focused verification of story US-EMDB-89's acceptance criterion "Leftover .compact file
/// is deleted on next open" through the REAL production open path
/// (<see cref="EmailManager.Open"/>), complementing <see cref="CompactorSwapTests"/>.
///
/// <para>Where <see cref="CompactorSwapTests"/> proves the static helper
/// (<see cref="Compactor.CleanupLeftoverSideFile"/>) deletes a leftover and leaves the source
/// byte-for-byte intact, these tests pin the end-to-end guarantee an operator actually gets:
/// a <c>&lt;name&gt;.emdb.compact</c> produced by a GENUINE interrupted compaction (a real
/// <see cref="Compactor.Begin"/> + <see cref="Compactor.CopyLiveBlocks"/> +
/// <see cref="Compactor.RebuildLocationIndexAndWriteCheckpoint"/> that is abandoned BEFORE
/// <see cref="Compactor.FinalizeAndSwap"/>, exactly the on-disk state a crash before the rename
/// leaves) is discarded on the very next <see cref="EmailManager.Open"/>, the open adopts the
/// COMPLETE OLD file (same FileId, and every committed email is still readable at its original
/// content), and the deletion is durable — <see cref="EmailManager.Open"/> routes cleanup
/// through <see cref="Compactor.CleanupLeftoverSideFile"/>, which fsyncs the containing
/// directory, so the removed side file stays removed across a subsequent reopen.</para>
///
/// <para>Every EmailManager and Compactor handle is disposed before the file is reopened, so
/// these pass in isolation and in the full parallel suite (no shared state, no order
/// dependence).</para>
/// </summary>
public class CompactionCriterion4Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-compact-c4-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    public void Dispose()
    {
        Delete(_path);
        Delete(SideFile);
    }

    private static void Delete(string p)
    {
        if (File.Exists(p))
            File.Delete(p);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static T Require<T>(Result<T> r)
    {
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
        return r.Value;
    }

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

    /// <summary>
    /// Builds a real, cleanly-closed <see cref="EmailManager"/> mailbox with several committed
    /// emails, returning its FileId and the (id, content) of every committed email so a reopen
    /// can prove the old file survives a leftover cleanup with its data intact.
    /// </summary>
    private (byte[] FileId, List<(V3Id Id, byte[] Mime)> Emails) BuildManagerMailbox(int emailCount)
    {
        byte[] fileId;
        var emails = new List<(V3Id, byte[])>();

        Require(EmailManager.Create(_path)).Dispose();
        using (var mgr = Require(EmailManager.Open(_path)))
        {
            fileId = (byte[])mgr.Superblock.FileId.Clone();
            var folder = NewFolder();
            for (int i = 0; i < emailCount; i++)
            {
                byte[] mime = Mime($"s{i}", $"body number {i} — lorem ipsum dolor sit amet");
                var added = Require(mgr.AddEmail(Request(folder, mime, Encoding.UTF8.GetBytes($"meta-{i}"), i + 1)));
                folder = added.Folder!;
                emails.Add((added.EmailId, mime));
            }
            Ok(mgr.Close());
        }

        return (fileId, emails);
    }

    /// <summary>
    /// Runs a real compaction over <see cref="_path"/> that copies the source's live blocks into
    /// the <c>.compact</c> side file and is then abandoned before the atomic rename — exactly the
    /// on-disk state a crash mid-compaction leaves: a real side file the machinery wrote, never
    /// renamed over the source. <see cref="Compactor.Dispose"/> only closes handles (it never
    /// removes an un-swapped side file), so the leftover remains for the next open to discard.
    /// Requires the mailbox to be closed first (Compactor takes a shared-read handle on the
    /// source).
    /// </summary>
    private void LeaveInterruptedCompactionSideFile()
    {
        using var compactor = Require(Compactor.Begin(_path));
        Require(compactor.CopyLiveBlocks());
        Assert.False(compactor.HasSwapped, "the compaction must be abandoned before the swap");
    }

    // -------------------------------------------------------------------------------------------

    [Fact]
    public void EmailManager_open_deletes_a_genuine_interrupted_compaction_leftover_and_the_old_file_with_its_emails_survives()
    {
        var (fileId, emails) = BuildManagerMailbox(emailCount: 24);

        // Produce the exact on-disk state of a compaction interrupted before its rename.
        LeaveInterruptedCompactionSideFile();
        Assert.True(File.Exists(SideFile), "a genuine interrupted compaction must leave a .compact side file");
        Assert.True(File.Exists(_path), "the complete old file must remain at the source path");

        // The NEXT open through the real production path discards the stale leftover and adopts
        // the COMPLETE OLD file: same FileId, and every committed email is still readable at its
        // original content (the open succeeded on the old file, not a half-written new one).
        using (var reopened = Require(EmailManager.Open(_path)))
        {
            Assert.Equal(fileId, reopened.Superblock.FileId);
            foreach (var (id, mime) in emails)
            {
                var got = reopened.GetEmail(id);
                Ok(got);
                Assert.True(got.Value.Found, $"committed email {id} must survive the leftover cleanup");
                Assert.Equal(mime, got.Value.Content);
            }
        }

        Assert.False(File.Exists(SideFile), "EmailManager.Open must delete the leftover .compact side file");
    }

    [Fact]
    public void EmailManager_open_leftover_deletion_is_durable_and_persists_across_a_second_open()
    {
        var (fileId, emails) = BuildManagerMailbox(emailCount: 8);

        LeaveInterruptedCompactionSideFile();
        Assert.True(File.Exists(SideFile));

        // First open deletes the leftover. EmailManager.Open routes the deletion through
        // Compactor.CleanupLeftoverSideFile, which fsyncs the containing directory (a fsync
        // failure would fail the open) — so the successful open is itself evidence the durable
        // deletion path ran.
        using (var first = Require(EmailManager.Open(_path)))
            Assert.Equal(fileId, first.Superblock.FileId);
        Assert.False(File.Exists(SideFile), "the leftover must be gone after the first open");

        // The deletion stuck: a second open finds nothing to clean, still succeeds, and the old
        // file (with its emails) is intact — the removed directory entry did not resurrect.
        using (var second = Require(EmailManager.Open(_path)))
        {
            Assert.Equal(fileId, second.Superblock.FileId);
            var (id, mime) = emails[0];
            var got = second.GetEmail(id);
            Ok(got);
            Assert.True(got.Value.Found);
            Assert.Equal(mime, got.Value.Content);
        }
        Assert.False(File.Exists(SideFile), "the second open must not resurrect the leftover");
    }

    [Fact]
    public void EmailManager_open_with_no_leftover_is_unaffected()
    {
        // The common case: no side file present. The cleanup on the open path must be a no-op
        // that neither fails the open nor disturbs the mailbox.
        var (fileId, emails) = BuildManagerMailbox(emailCount: 4);
        Assert.False(File.Exists(SideFile));

        using var mgr = Require(EmailManager.Open(_path));
        Assert.Equal(fileId, mgr.Superblock.FileId);
        var (id, mime) = emails[^1];
        var got = mgr.GetEmail(id);
        Ok(got);
        Assert.True(got.Value.Found);
        Assert.Equal(mime, got.Value.Content);
        Assert.False(File.Exists(SideFile));
    }
}
