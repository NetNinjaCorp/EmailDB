using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for group-commit batching (US-EMDB-85-6): multiple <see cref="EmailManager.AddEmail"/>
/// calls share one flush + Checkpoint, an explicit <see cref="EmailManager.Commit"/> makes a batch
/// durable, an <see cref="EmailManager.AutoCommitThreshold"/> bounds the Checkpoint count of a bulk
/// load, and <see cref="EmailManager.AddEmails"/> is the one-shot bulk-add path.
///
/// <para>The decisive dimension these cover that the single-add pipeline tests do not is
/// <b>durability across reopen</b>: a committed AddEmail seeds the PrimaryEmail + Date indexes from
/// the Checkpoint's durable index roots on the next Open, so a committed email survives reopen, is
/// observed by every index, and cross-session dedupe catches a re-add — closing the reopen gap the
/// task was written to fix.</para>
/// </summary>
public class EmailManagerGroupCommitV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-groupcommit-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(int n) =>
        Encoding.UTF8.GetBytes($"From: s{n}@example.com\r\nSubject: probe {n}\r\n\r\nbody number {n} bytes");

    private static FolderPageDirectory NewFolder() =>
        FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

    private static AddEmailRequest Request(FolderPageDirectory folder, byte[] mime, long ticks) => new()
    {
        RawContent = mime,
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = "sender@example.com",
        Subject = "probe",
        Preview = "preview",
    };

    /// <summary>Resolves a BlockId to its live file offset through the composed resolver (runtime map → location index).</summary>
    private static long OffsetOf(EmailManager mgr, byte[] blockId)
    {
        Assert.True(mgr.Resolver!.TryGetLocation(blockId, out var loc) && loc is not null,
            "block did not resolve to a file offset");
        return loc!.Offset;
    }

    /// <summary>
    /// Reconstructs a folder's effective listing the read path serves (docs/Folder_Listing.md Sections 2-3):
    /// reads every compiled page, walks the pending delta chain head→root, and merges. Uses ONLY the
    /// resolver + folder stores exposed on the manager, so it observes the same durable state a browser would.
    /// </summary>
    private static IReadOnlyList<ListingRecord> EffectiveListing(EmailManager mgr, FolderPageDirectory folder)
    {
        var pageRecords = new List<ListingRecord>();
        foreach (var entry in folder.PageEntries)
        {
            var page = mgr.Folders!.ReadPage(OffsetOf(mgr, entry.PageBlockId));
            Ok(page);
            pageRecords.AddRange(page.Value.Records);
        }

        var chainHeadToRoot = new List<FolderDeltaLog>();
        var cursor = folder.HasPendingDelta ? folder.HeadDeltaBlockId : null;
        while (cursor is not null)
        {
            var read = mgr.FolderDeltas!.ReadDeltaBlock(OffsetOf(mgr, cursor));
            Ok(read);
            chainHeadToRoot.Add(read.Value);
            cursor = read.Value.HasPrevious ? read.Value.PreviousDeltaBlockId : null;
        }

        return FolderListingMerger.Merge(pageRecords, chainHeadToRoot);
    }

    /// <summary>Re-reads a folder's current directory version from the file by resolving its stable FolderId BlockId.</summary>
    private static FolderPageDirectory ReloadDirectory(EmailManager mgr, byte[] folderId)
    {
        var dir = mgr.FolderDirectory!.ReadDirectory(OffsetOf(mgr, folderId));
        Ok(dir);
        return dir.Value;
    }

    // ---------------------------------------------------- A committed add survives reopen

    [Fact]
    public void A_committed_AddEmail_survives_reopen_and_every_index_observes_it()
    {
        long ticks = new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc).Ticks;
        byte[] mime = Mime(1);
        V3Id id;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
            Ok(added);
            Assert.False(added.Value.IsDuplicate);
            id = added.Value.EmailId;
            Ok(mgr.Commit()); // explicit group commit
        }

        // Reopen via the clean fast path: the PrimaryEmail index is seeded from the committed
        // Checkpoint's durable root, so the email's identity resolves to its ContentBlockId.
        using (var reopened = EmailManager.Open(_path).Value)
        {
            var lookup = reopened.PrimaryIndex!.TryGet(id.GetBytes());
            Ok(lookup);
            Assert.True(lookup.Value.Found, "a committed email must be visible in the primary index after reopen.");

            // The Date index observes it too (range query over the reopened, seeded tree).
            var range = reopened.DateIndex!.RangeQuery(ticks, ticks);
            Ok(range);
            Assert.Single(range.Value);
            Assert.Equal(ticks, range.Value[0].DateTicks);
            Assert.Equal(lookup.Value.Value, range.Value[0].BlockId);

            // The Tier 3 content block resolves and reads back the exact MIME.
            Assert.True(reopened.Resolver!.TryGetLocation(lookup.Value.Value!, out var loc) && loc is not null);
            var content = reopened.BlockManager.Read(loc!.Offset);
            Ok(content);
            Assert.Equal(mime, content.Value.Payload);
        }
    }

    [Fact]
    public void Every_observation_surface_sees_a_committed_email_in_the_same_session_without_reopen()
    {
        long ticks = new DateTime(2026, 7, 10, 15, 0, 0, DateTimeKind.Utc).Ticks;
        byte[] mime = Mime(3);

        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
        Ok(added);
        var r = added.Value;
        Assert.False(r.IsDuplicate);

        // Commit in this SAME session (no reopen). Commit folds every append into the durable location
        // index and clears the runtime map, so from here every surface observes the email through the
        // committed durable state, not a still-buffered runtime append.
        Ok(mgr.Commit());

        // 1. Primary index: EmailHashedID → ContentBlockId.
        var lookup = mgr.PrimaryIndex!.TryGet(r.EmailId.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found, "the primary index must observe the email after commit.");
        Assert.Equal(r.ContentBlockId, lookup.Value.Value);

        // 2. Date index: a range query spanning the timestamp resolves to the ContentBlockId.
        var range = mgr.DateIndex!.RangeQuery(ticks, ticks);
        Ok(range);
        Assert.Single(range.Value);
        Assert.Equal(ticks, range.Value[0].DateTicks);
        Assert.Equal(r.ContentBlockId, range.Value[0].BlockId);

        // 3. Location index: BOTH the Tier 3 content and Tier 2 metadata BlockIds resolve to readable blocks.
        var content = mgr.BlockManager.Read(OffsetOf(mgr, r.ContentBlockId!));
        Ok(content);
        Assert.Equal(BlockType.EmailContent, content.Value.Header.Type);
        Assert.Equal(mime, content.Value.Payload);
        var meta = mgr.BlockManager.Read(OffsetOf(mgr, r.MetadataBlockId!));
        Ok(meta);
        Assert.Equal(BlockType.EmailMetadata, meta.Value.Header.Type);

        // 4. Folder listing: the target folder's effective listing contains exactly this email's row.
        var listing = EffectiveListing(mgr, r.Folder!);
        var row = Assert.Single(listing);
        Assert.Equal(r.EmailId, row.EmailHashedId);
        Assert.Equal(r.ContentBlockId, row.ContentBlockId);
        Assert.Equal(ticks, row.DateTicks);
    }

    [Fact]
    public void Committed_emails_in_multiple_folders_are_each_observed_only_in_their_own_folder_listing()
    {
        byte[] mimeA = Mime(100);
        byte[] mimeB = Mime(200);
        long ticksA = 500_000_000_000_000_000L;
        long ticksB = 600_000_000_000_000_000L;
        V3Id idA, idB;
        byte[] folderIdA, folderIdB;
        FolderPageDirectory dirA, dirB;

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var folderA = NewFolder();
            var folderB = NewFolder();
            folderIdA = folderA.FolderId;
            folderIdB = folderB.FolderId;

            var addedA = mgr.AddEmail(Request(folderA, mimeA, ticksA));
            Ok(addedA);
            var addedB = mgr.AddEmail(Request(folderB, mimeB, ticksB));
            Ok(addedB);
            idA = addedA.Value.EmailId;
            idB = addedB.Value.EmailId;
            dirA = addedA.Value.Folder!;
            dirB = addedB.Value.Folder!;
            Assert.NotEqual(idA, idB);

            Ok(mgr.Commit());

            // Same session: each folder's listing holds ONLY its own email — no cross-folder bleed.
            var listingA = EffectiveListing(mgr, dirA);
            var listingB = EffectiveListing(mgr, dirB);
            Assert.Equal(idA, Assert.Single(listingA).EmailHashedId);
            Assert.Equal(idB, Assert.Single(listingB).EmailHashedId);
            Assert.DoesNotContain(listingA, row => row.EmailHashedId == idB);
            Assert.DoesNotContain(listingB, row => row.EmailHashedId == idA);
        }

        // Cross session: reopen and re-read each folder's directory from the file by its FolderId. Both
        // emails are observed by the seeded primary + date indexes, and each folder listing still holds
        // only its own email.
        using (var reopened = EmailManager.Open(_path).Value)
        {
            Assert.True(reopened.PrimaryIndex!.TryGet(idA.GetBytes()).Value.Found);
            Assert.True(reopened.PrimaryIndex!.TryGet(idB.GetBytes()).Value.Found);
            var all = reopened.DateIndex!.RangeQuery(0, long.MaxValue);
            Ok(all);
            Assert.Equal(2, all.Value.Count);

            var reloadedA = ReloadDirectory(reopened, folderIdA);
            var reloadedB = ReloadDirectory(reopened, folderIdB);
            var listingA = EffectiveListing(reopened, reloadedA);
            var listingB = EffectiveListing(reopened, reloadedB);
            Assert.Equal(idA, Assert.Single(listingA).EmailHashedId);
            Assert.Equal(idB, Assert.Single(listingB).EmailHashedId);
            Assert.DoesNotContain(listingA, row => row.EmailHashedId == idB);
            Assert.DoesNotContain(listingB, row => row.EmailHashedId == idA);
        }
    }

    [Fact]
    public void Cross_session_dedupe_catches_a_re_add_of_a_committed_email()
    {
        byte[] mime = Mime(42);

        EmailManager.Create(_path).Value.Dispose();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            Ok(mgr.AddEmail(Request(NewFolder(), mime, 1000)));
            Ok(mgr.Close()); // Close commits.
        }

        // A brand-new session re-adds the identical bytes: the seeded primary index catches the
        // content hash up front — no second content block, the same identity reported.
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var readd = mgr.AddEmail(Request(NewFolder(), mime, 2000));
            Ok(readd);
            Assert.True(readd.Value.IsDuplicate, "re-adding a committed email in a new session must be a duplicate.");
            Assert.Null(readd.Value.ContentBlockId);
            Assert.Equal(0, mgr.PrimaryIndex!.PendingCount); // nothing buffered for a duplicate.
        }
    }

    [Fact]
    public void A_committed_AddEmail_survives_a_crash_without_a_clean_Close()
    {
        byte[] mime = Mime(7);
        V3Id id;

        EmailManager.Create(_path).Value.Dispose();
        var mgr = EmailManager.Open(_path).Value;
        var added = mgr.AddEmail(Request(NewFolder(), mime, 555));
        Ok(added);
        id = added.Value.EmailId;
        Ok(mgr.Commit());
        mgr.Dispose(); // process death after a commit: no Close, but the commit already made it durable.

        // The clean fast path resolves the committed Checkpoint (Commit repointed the superblock hint).
        using var reopened = EmailManager.Open(_path).Value;
        var lookup = reopened.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found, "an email committed before a crash must survive to the next clean open.");
    }

    [Fact]
    public void A_committed_add_survives_the_dirty_recovery_open_path_after_a_kill9()
    {
        byte[] mime = Mime(11);
        V3Id id;

        EmailManager.Create(_path).Value.Dispose();
        var mgr = EmailManager.Open(_path).Value;
        var added = mgr.AddEmail(Request(NewFolder(), mime, 909));
        Ok(added);
        id = added.Value.EmailId;
        Ok(mgr.Commit());
        mgr.Dispose();

        // Simulate a kill -9 AFTER the commit: stamp CleanShutdown = 0 so the reopen is forced down
        // the recovery (dirty-open) path rather than the clean fast path.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            Ok(sbManager.Write(dirty));
        }

        // The reopen genuinely needs recovery, and recovery still seeds the primary index from the
        // adopted Checkpoint's durable root.
        using (var probe = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var clean = CleanOpener.Open(probe);
            Ok(clean);
            Assert.Equal(OpenOutcomeKind.DirtyOpenRequired, clean.Value.Kind);
        }

        using var recovered = EmailManager.Open(_path).Value;
        var lookup = recovered.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found, "a committed email must survive crash recovery via the dirty-open path.");
    }

    // ---------------------------------------------------- Group commit bounds Checkpoint count

    // A bulk add of `total` fresh emails auto-commits every `threshold` adds, so it produces EXACTLY
    // one Checkpoint per full batch plus one final Close Checkpoint — never one Checkpoint per email.
    // Two thresholds prove the batching tracks the custom AutoCommitThreshold rather than a fixed cadence:
    //  - 256 (default-sized): 1000/256 = 3 auto-commits + 1 Close  = 4 Checkpoints (trailing 232 partial batch)
    //  - 100:                 1000/100 = 10 auto-commits + 1 Close  = 11 Checkpoints (evenly divisible)
    [Theory]
    [InlineData(256, 4)]
    [InlineData(100, 11)]
    public void A_thousand_email_bulk_add_commits_in_batches_with_a_bounded_checkpoint_count(
        int threshold, ulong expectedCheckpoints)
    {
        const int total = 1000;
        var ticks = new long[total];
        var ids = new V3Id[total];

        EmailManager.Create(_path).Value.Dispose();
        ulong baselineSequence, finalSequence;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            mgr.AutoCommitThreshold = threshold; // at most one Checkpoint per `threshold` fresh emails.
            baselineSequence = mgr.LastCheckpointSequence; // the Create Checkpoint (sequence 0).

            FolderPageDirectory folder = NewFolder();
            for (int i = 0; i < total; i++)
            {
                ticks[i] = 638_000_000_000_000_000L + i;
                var added = mgr.AddEmail(Request(folder, Mime(i), ticks[i]));
                Ok(added);
                Assert.False(added.Value.IsDuplicate);
                ids[i] = added.Value.EmailId;
                folder = added.Value.Folder!; // thread the advanced directory
            }
            Ok(mgr.Close());
            finalSequence = mgr.LastCheckpointSequence;
        }

        // Checkpoint count is EXACT, measured as the Checkpoint-sequence delta since Create: each
        // Commit/Close advances LastCheckpointSequence by exactly one, so the delta IS the number of
        // Checkpoints this bulk add wrote. It is total/threshold auto-commits + 1 Close Checkpoint —
        // orders of magnitude below `total`, and it tracks the custom threshold exactly (not one per email).
        ulong checkpointsWritten = finalSequence - baselineSequence;
        Assert.Equal(expectedCheckpoints, checkpointsWritten);
        Assert.Equal((ulong)(total / threshold) + 1, checkpointsWritten); // = auto-commits + final Close

        // Every committed email survives reopen: all 1000 identities resolve in the primary index, and
        // the date index counts exactly 1000 rows (a full listing, not spot checks).
        using (var reopened = EmailManager.Open(_path).Value)
        {
            Assert.Equal(finalSequence, reopened.LastCheckpointSequence);
            for (int i = 0; i < total; i++)
            {
                var lookup = reopened.PrimaryIndex!.TryGet(ids[i].GetBytes());
                Ok(lookup);
                Assert.True(lookup.Value.Found, $"email {i} committed in the bulk add must survive reopen.");
            }
            var all = reopened.DateIndex!.RangeQuery(0, long.MaxValue);
            Ok(all);
            Assert.Equal(total, all.Value.Count);
        }
    }

    // ---------------------------------------------------- Bulk-add path (AddEmails)

    [Fact]
    public void AddEmails_bulk_path_commits_once_and_every_email_survives_reopen()
    {
        const int total = 250;
        var requests = new List<AddEmailRequest>(total);
        var folder = NewFolder();
        for (int i = 0; i < total; i++)
            requests.Add(Request(folder, Mime(1000 + i), 700_000_000_000_000_000L + i));

        EmailManager.Create(_path).Value.Dispose();
        var idBytes = new List<byte[]>();
        ulong sequenceAfterBulk;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            mgr.AutoCommitThreshold = 10_000; // large: force a single group commit at the end.
            var bulk = mgr.AddEmails(requests);
            Ok(bulk);
            Assert.Equal(total, bulk.Value.Count);
            foreach (var r in bulk.Value)
            {
                Assert.False(r.IsDuplicate);
                idBytes.Add(r.EmailId.GetBytes());
            }
            sequenceAfterBulk = mgr.LastCheckpointSequence;
            Ok(mgr.Close());
        }

        // One group commit for the whole batch (sequence advanced from 0 to exactly 1).
        Assert.Equal(1UL, sequenceAfterBulk);

        using (var reopened = EmailManager.Open(_path).Value)
        {
            foreach (var key in idBytes)
            {
                var lookup = reopened.PrimaryIndex!.TryGet(key);
                Ok(lookup);
                Assert.True(lookup.Value.Found);
            }
            var all = reopened.DateIndex!.RangeQuery(0, long.MaxValue);
            Ok(all);
            Assert.Equal(total, all.Value.Count);
        }
    }

    // ---------------------------------------------------- Uncommitted adds do not survive

    [Fact]
    public void Adds_after_the_last_commit_that_are_not_committed_do_not_survive_a_crash()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] committedMime = Mime(1);
        byte[] uncommittedMime = Mime(2);
        V3Id committedId, uncommittedId;

        var mgr = EmailManager.Open(_path).Value;
        mgr.AutoCommitThreshold = 10_000; // no auto-commit interferes.
        var first = mgr.AddEmail(Request(NewFolder(), committedMime, 100));
        Ok(first);
        committedId = first.Value.EmailId;
        Ok(mgr.Commit()); // commit the first email only.

        var second = mgr.AddEmail(Request(NewFolder(), uncommittedMime, 200));
        Ok(second);
        uncommittedId = second.Value.EmailId; // buffered but NOT committed.
        mgr.Dispose(); // crash: the second add never reached a Checkpoint.

        using var reopened = EmailManager.Open(_path).Value;
        var committed = reopened.PrimaryIndex!.TryGet(committedId.GetBytes());
        Ok(committed);
        Assert.True(committed.Value.Found, "the committed email must survive.");

        var uncommitted = reopened.PrimaryIndex!.TryGet(uncommittedId.GetBytes());
        Ok(uncommitted);
        Assert.False(uncommitted.Value.Found, "an add made after the last commit must not survive the crash.");
    }

    // ---------------------------------------------------- Multiple commits within a session

    [Fact]
    public void Multiple_explicit_commits_in_one_session_all_survive_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        var ids = new List<byte[]>();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            mgr.AutoCommitThreshold = 10_000;
            var folder = NewFolder();
            for (int batch = 0; batch < 3; batch++)
            {
                for (int i = 0; i < 5; i++)
                {
                    var added = mgr.AddEmail(Request(folder, Mime(batch * 100 + i), 10_000 + batch * 100 + i));
                    Ok(added);
                    ids.Add(added.Value.EmailId.GetBytes());
                    folder = added.Value.Folder!;
                }
                Ok(mgr.Commit()); // a fresh Checkpoint per batch; the location index is re-folded correctly.
            }
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path).Value;
        foreach (var key in ids)
        {
            var lookup = reopened.PrimaryIndex!.TryGet(key);
            Ok(lookup);
            Assert.True(lookup.Value.Found);
        }
        var all = reopened.DateIndex!.RangeQuery(0, long.MaxValue);
        Ok(all);
        Assert.Equal(ids.Count, all.Value.Count);
    }

    // ------------------------------- Full-fidelity recovery via the dirty-open path

    /// <summary>
    /// Strengthens <see cref="A_committed_add_survives_the_dirty_recovery_open_path_after_a_kill9"/>
    /// (which asserts only primary-index presence) to the full recovery contract the acceptance
    /// criterion implies: after a kill -9 forces the bounded dirty-open + WAL-replay path, EVERY
    /// observation surface a browser would use is intact — the exact MIME content bytes, the Tier 2
    /// metadata block, the Date index row, AND the folder's effective listing — not merely an index hit.
    /// </summary>
    [Fact]
    public void A_committed_add_recovered_via_the_dirty_open_path_has_content_metadata_and_listing_intact()
    {
        long ticks = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
        byte[] mime = Mime(77);
        V3Id id;
        byte[] contentBlockId, metadataBlockId, folderId;

        EmailManager.Create(_path).Value.Dispose();
        var mgr = EmailManager.Open(_path).Value;
        var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
        Ok(added);
        id = added.Value.EmailId;
        contentBlockId = added.Value.ContentBlockId!;
        metadataBlockId = added.Value.MetadataBlockId!;
        folderId = added.Value.Folder!.FolderId;
        Ok(mgr.Commit());
        mgr.Dispose();

        // kill -9: stamp CleanShutdown = 0 so the reopen genuinely takes the recovery (dirty-open) path.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            Ok(sbManager.Write(dirty));
        }
        using (var probe = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var clean = CleanOpener.Open(probe);
            Ok(clean);
            Assert.Equal(OpenOutcomeKind.DirtyOpenRequired, clean.Value.Kind);
        }

        using var recovered = EmailManager.Open(_path).Value;

        // 1. Primary index resolves the identity to its ContentBlockId.
        var lookup = recovered.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found, "the committed email must survive the dirty-open recovery path.");
        Assert.Equal(contentBlockId, lookup.Value.Value);

        // 2. Tier 3 content: the exact MIME bytes read back through the recovered resolver.
        var content = recovered.BlockManager.Read(OffsetOf(recovered, contentBlockId));
        Ok(content);
        Assert.Equal(BlockType.EmailContent, content.Value.Header.Type);
        Assert.Equal(mime, content.Value.Payload);

        // 3. Tier 2 metadata block still resolves and reads.
        var meta = recovered.BlockManager.Read(OffsetOf(recovered, metadataBlockId));
        Ok(meta);
        Assert.Equal(BlockType.EmailMetadata, meta.Value.Header.Type);

        // 4. Date index observes the row.
        var range = recovered.DateIndex!.RangeQuery(ticks, ticks);
        Ok(range);
        Assert.Equal(contentBlockId, Assert.Single(range.Value).BlockId);

        // 5. Folder listing: the target folder's effective listing (re-read from the file by FolderId)
        //    still holds exactly this email's row — recovery restored the folder view, not just indexes.
        var listing = EffectiveListing(recovered, ReloadDirectory(recovered, folderId));
        Assert.Equal(id, Assert.Single(listing).EmailHashedId);
    }

    // ---------------------------------------------------- Encrypted file

    /// <summary>
    /// DOCUMENTS A DEFECT (US-EMDB-85-2 finding, currently <b>skipped</b>): a committed AddEmail on an
    /// encrypted file MUST survive a kill -9 that forces the dirty-open path, with the password supplied
    /// (spec Section 10.2 step 2 — recovery decrypts through the encryption bootstrap seam).
    ///
    /// <para><b>Observed instead:</b> the open FAILS with <i>"Dirty open healed the file but the clean
    /// re-open reported EncryptionBootstrapRequired"</i>. Root cause: <see cref="DirtyOpener"/> heals the
    /// superblock, then re-runs the clean fast path as <c>CleanOpener.Open(stream)</c> (DirtyOpener.cs
    /// ~line 257) WITHOUT forwarding the <c>IEncryptionBootstrap</c> it was given, so the healed re-open
    /// cannot decrypt. Encrypted files can therefore never recover via the dirty-open path, losing access
    /// to the committed email. Fix: forward the bootstrap into the post-heal <c>CleanOpener.Open</c>.
    /// Unskip once fixed.</para>
    /// </summary>
    [Fact]
    public void A_committed_add_on_an_encrypted_file_survives_a_kill9_dirty_recovery()
    {
        const string password = "encrypted crash recovery password";
        byte[] mime = Mime(88);
        V3Id id;
        byte[] contentBlockId;

        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = password,
            KdfParameters = new Argon2idParams(8, 1, 1),
        }).Value.Dispose();

        var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password }).Value;
        var added = mgr.AddEmail(Request(NewFolder(), mime, 4321));
        Ok(added);
        id = added.Value.EmailId;
        contentBlockId = added.Value.ContentBlockId!;
        Ok(mgr.Commit());
        mgr.Dispose();

        // kill -9 on the encrypted file: the superblock rewrite preserves the encryption bootstrap
        // fields (salt/KdfParams/token) — only CleanShutdown flips to 0.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            Ok(sbManager.Write(dirty));
        }

        var recoveredOpen = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password });
        Ok(recoveredOpen);
        using var recovered = recoveredOpen.Value;
        Assert.True(recovered.IsEncrypted);
        var lookup = recovered.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found, "a committed email on an encrypted file must survive crash recovery.");
        // Read the Tier 3 content back through the DECRYPTING read path (GetEmail): the on-disk
        // EmailContent block is ciphertext for an encrypted file, so a raw BlockManager.Read would
        // return ciphertext — the exact plaintext MIME is recovered only through the DEK provider that
        // dirty-open recovery loaded via the forwarded encryption bootstrap.
        var got = recovered.GetEmail(id);
        Ok(got);
        Assert.True(got.Value.Found, "the committed email's content must survive crash recovery on an encrypted file.");
        Assert.Equal(mime, got.Value.Content); // decrypted plaintext MIME, intact.
        _ = contentBlockId;
    }

    // ------------------------------- Torn checkpoint / dangling-WAL recovery (KNOWN DEFECT)

    /// <summary>
    /// DOCUMENTS A DEFECT (US-EMDB-85-2 finding, currently <b>skipped</b>): a crash mid-commit that
    /// tears the newest Checkpoint while a durable earlier commit and its post-checkpoint WAL survive
    /// MUST, per spec Section 13 ("Checkpoint invalid / torn commit → walk PreviousCheckpointBlockId
    /// chain; post-checkpoint blocks are uncommitted <i>except matching WAL blocks (replayed)</i>") and
    /// Section 10.4, fall back to the previous Checkpoint and replay the matching WAL — so the earlier
    /// committed email stays readable and the WAL-logged one is recovered.
    ///
    /// <para><b>Observed instead:</b> <see cref="EmailManager.Open"/> takes the dirty-open path (line
    /// ~960) with NO WAL replay sink and NO fresh-Checkpoint contents, so <see cref="DirtyOpener"/>
    /// replays the WAL fenced to the adopted Checkpoint into a discard sink, cannot commit it
    /// (<c>HealedClean = false</c>), and the whole open FAILS: <i>"recovery replayed uncommitted WAL that
    /// could not be committed to a clean point"</i>. The previously-committed email A is thereby LOST
    /// (the file is unopenable), violating the acceptance criterion on the dirty/torn-checkpoint path.
    /// The clean-open path masks this because <see cref="EmailManager"/> also violates spec Section 3.3
    /// (it never stamps CleanShutdown = 0 on first write), so real kill -9s usually reopen clean.</para>
    ///
    /// <para><b>Fix seam:</b> the DirtyOpenRequired branch of <see cref="EmailManager.Open"/> must
    /// supply a real <c>IWalReplaySink</c> (applying inserts into the primary/date/folder indexes) and a
    /// <c>freshCheckpointContents</c> factory so <see cref="DirtyOpener"/> commits the replayed WAL and
    /// heals the file clean. Unskip this test once that integration lands.</para>
    /// </summary>
    [Fact]
    public void A_torn_checkpoint_falls_back_to_the_previous_commit_and_replays_matching_WAL()
    {
        byte[] mimeA = Mime(1);
        byte[] mimeB = Mime(2);
        V3Id idA, idB;
        byte[] cp1BlockId;
        long cp1Offset;

        EmailManager.Create(_path).Value.Dispose();
        var mgr = EmailManager.Open(_path).Value;
        mgr.AutoCommitThreshold = 10_000;

        var a = mgr.AddEmail(Request(NewFolder(), mimeA, 100));
        Ok(a);
        idA = a.Value.EmailId;
        Ok(mgr.Commit()); // Checkpoint 1 — the durable earlier commit.
        cp1BlockId = (byte[])mgr.LastCheckpoint.BlockId.Clone();
        cp1Offset = mgr.LastCheckpoint.Offset;

        var b = mgr.AddEmail(Request(NewFolder(), mimeB, 200)); // B's WAL is fenced to Checkpoint 1.
        Ok(b);
        idB = b.Value.EmailId;
        Ok(mgr.Commit()); // Checkpoint 2 — the commit whose Checkpoint write we now tear.
        mgr.Dispose();

        long fileLength = new FileInfo(_path).Length;

        // Model the torn commit: the Checkpoint-2 block (the last append) is half-written and the
        // superblock update to point at it never happened. Reset the hint to Checkpoint 1 with
        // CleanShutdown = 0, then truncate the tail so Checkpoint 2 is unreadable — exactly the state a
        // crash between the data/WAL fsync and the Checkpoint fsync leaves on disk.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            dirty.LastCheckpointBlockId = cp1BlockId;
            dirty.LastCheckpointOffset = cp1Offset;
            Ok(sbManager.Write(dirty));
        }
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(fileLength - 4); // tear Checkpoint 2's tail
        }

        // Correct behavior (spec Section 13): recovery adopts Checkpoint 1, replays B's WAL fenced to it,
        // writes a fresh Checkpoint, and heals clean. BOTH emails are then readable.
        using var recovered = EmailManager.Open(_path).Value;
        Assert.True(recovered.PrimaryIndex!.TryGet(idA.GetBytes()).Value.Found,
            "the previously-committed email A must survive a torn Checkpoint 2 (fall back to Checkpoint 1).");
        Assert.True(recovered.PrimaryIndex!.TryGet(idB.GetBytes()).Value.Found,
            "email B, logged to the WAL fenced to Checkpoint 1, must be recovered by WAL replay (spec Section 13).");
    }

    // ---------------------------------------------------- Encrypted file

    [Fact]
    public void A_committed_add_on_an_encrypted_file_survives_reopen_with_the_password()
    {
        const string password = "group commit probe password";
        byte[] mime = Mime(9);
        V3Id id;

        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = password,
            KdfParameters = new Argon2idParams(8, 1, 1),
        }).Value.Dispose();

        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password }).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, 321));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close());
        }

        using var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password }).Value;
        Assert.True(reopened.IsEncrypted);
        var lookup = reopened.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found, "a committed email on an encrypted file must survive reopen.");
    }
}
