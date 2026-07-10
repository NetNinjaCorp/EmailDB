using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the remaining mailbox operations (story US-EMDB-87, task US-EMDB-87-5):
/// <see cref="EmailManager.MoveEmail"/>, <see cref="EmailManager.DeleteEmail"/>, and
/// <see cref="EmailManager.ChangeFlags"/>.
///
/// <para>The DoD is asserted directly against the on-disk deltas and indexes: a move is a
/// <c>Delete</c> delta in the source folder and an <c>Add</c> delta in the target folder with the
/// content/index untouched (no Tier 2/3 rewrite); a delete drops the folder row, removes the
/// PrimaryEmail + Date index entries, and moves the email's on-disk bytes live → dead; a flag change
/// is a <c>FlagChange</c> delta carrying the new flags (docs/Folder_Listing.md Section 3).</para>
/// </summary>
public class EmailManagerMoveDeleteFlagV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-movedelflag-{Guid.NewGuid():N}.emdb");

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

    private static ListingRecord RecordOf(AddEmailResult added, long ticks, byte[] mime) => new()
    {
        EmailHashedId = added.EmailId,
        ContentBlockId = added.ContentBlockId!,
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        MessageSize = mime.Length,
        From = "sender@example.com",
        Subject = "probe",
        Preview = "preview body text",
    };

    private static BlockLocation Locate(EmailManager mgr, byte[] blockId)
    {
        Assert.True(mgr.Resolver!.TryGetLocation(blockId, out var loc) && loc is not null,
            "appended block did not resolve to a file offset");
        return loc!;
    }

    private static FolderDeltaEntry HeadDelta(EmailManager mgr, byte[] deltaBlockId)
    {
        var delta = mgr.FolderDeltas!.ReadDeltaBlock(Locate(mgr, deltaBlockId).Offset);
        Ok(delta);
        return Assert.Single(delta.Value.Entries);
    }

    // --------------------------------------------------------- Move

    [Fact]
    public void Move_appears_in_both_folders_as_delete_plus_add_without_rewriting_content()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var source = NewFolder();
        byte[] mime = Mime("Move", "body to move between folders");
        long ticks = 424242;

        var added = mgr.AddEmail(Request(source, mime, ticks));
        Ok(added);
        source = added.Value.Folder!;
        var target = NewFolder();

        // Capture the content block's exact on-disk bytes BEFORE the move.
        var contentLoc = Locate(mgr, added.Value.ContentBlockId!);
        var contentBefore = mgr.BlockManager.Read(contentLoc.Offset);
        Ok(contentBefore);
        byte[] rawBefore = (byte[])contentBefore.Value.Payload.Clone();

        var move = mgr.MoveEmail(new MoveEmailRequest
        {
            SourceFolder = source,
            TargetFolder = target,
            Record = RecordOf(added.Value, ticks, mime),
        });
        Ok(move);

        // Source folder's new delta head is a Delete for the email; target's is an Add of the same row.
        Assert.Equal(source.FolderVersion + 1, move.Value.SourceFolder.FolderVersion);
        Assert.Equal(target.FolderVersion + 1, move.Value.TargetFolder.FolderVersion);

        var srcEntry = HeadDelta(mgr, move.Value.SourceDeltaBlockId);
        Assert.Equal(FolderDeltaOp.Delete, srcEntry.Op);
        Assert.Equal(added.Value.EmailId, srcEntry.EmailHashedId);

        var tgtEntry = HeadDelta(mgr, move.Value.TargetDeltaBlockId);
        Assert.Equal(FolderDeltaOp.Add, tgtEntry.Op);
        Assert.Equal(added.Value.EmailId, tgtEntry.EmailHashedId);
        Assert.NotNull(tgtEntry.Record);
        // Same ContentBlockId in both folders — no new content block was written for the move.
        Assert.Equal(added.Value.ContentBlockId, tgtEntry.Record!.ContentBlockId);

        // The Tier 3 content block is byte-for-byte untouched and still readable through GetEmail.
        var contentAfter = mgr.BlockManager.Read(contentLoc.Offset);
        Ok(contentAfter);
        Assert.Equal(rawBefore, contentAfter.Value.Payload);
        var read = mgr.GetEmail(added.Value.EmailId);
        Ok(read);
        Assert.True(read.Value.Found);
        Assert.Equal(mime, read.Value.Content);
    }

    [Fact]
    public void Move_does_not_touch_the_primary_or_date_indexes()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var source = NewFolder();
        byte[] mime = Mime("MoveIdx", "still one identity after a move");
        long ticks = 999000;

        var added = mgr.AddEmail(Request(source, mime, ticks));
        Ok(added);

        var move = mgr.MoveEmail(new MoveEmailRequest
        {
            SourceFolder = added.Value.Folder!,
            TargetFolder = NewFolder(),
            Record = RecordOf(added.Value, ticks, mime),
        });
        Ok(move);

        // The identity still dedupes (primary index unchanged) and still range-queries (date index unchanged).
        var lookup = mgr.PrimaryIndex!.TryGet(added.Value.EmailId.GetBytes());
        Ok(lookup);
        Assert.True(lookup.Value.Found);
        Assert.Equal(added.Value.ContentBlockId, lookup.Value.Value);

        var range = mgr.DateIndex!.RangeQuery(ticks, ticks);
        Ok(range);
        Assert.Single(range.Value);
        Assert.Equal(added.Value.ContentBlockId, range.Value[0].BlockId);
    }

    [Fact]
    public void Move_grows_the_file_only_by_delta_and_directory_blocks()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var source = NewFolder();
        byte[] mime = Mime("MoveGrow", "physical proof: nothing but Tier 1 bookkeeping is appended");
        long ticks = 202020;
        var added = mgr.AddEmail(Request(source, mime, ticks));
        Ok(added);
        source = added.Value.Folder!;
        long contentOffsetBefore = Locate(mgr, added.Value.ContentBlockId!).Offset;

        // Physically enumerate every block in the file before the move.
        var before = mgr.BlockManager.ScanForward();
        Ok(before);
        Assert.Empty(before.Value.DamagedRanges);

        var move = mgr.MoveEmail(new MoveEmailRequest
        {
            SourceFolder = source,
            TargetFolder = NewFolder(),
            Record = RecordOf(added.Value, ticks, mime),
        });
        Ok(move);

        // Re-enumerate: the move appended exactly two FolderDeltaLog blocks (source Delete + target
        // Add) and two FolderPageDirectory blocks (the two advanced directories) — no EmailContent,
        // no EmailMetadata, nothing else. The file grew by exactly those blocks' bytes.
        var after = mgr.BlockManager.ScanForward();
        Ok(after);
        Assert.Empty(after.Value.DamagedRanges);

        var newBlocks = after.Value.Blocks.Skip(before.Value.Blocks.Count).ToList();
        Assert.Equal(4, newBlocks.Count);
        Assert.Equal(
            before.Value.FileLength + newBlocks.Sum(b => b.TotalBlockLength),
            after.Value.FileLength);

        var newTypes = newBlocks
            .Select(b => { var r = mgr.BlockManager.Read(b.Offset); Ok(r); return r.Value.Header.Type; })
            .ToList();
        Assert.Equal(2, newTypes.Count(t => t == BlockType.FolderDeltaLog));
        Assert.Equal(2, newTypes.Count(t => t == BlockType.FolderPageDirectory));
        Assert.DoesNotContain(BlockType.EmailContent, newTypes);
        Assert.DoesNotContain(BlockType.EmailMetadata, newTypes);

        // The Tier 3 content block was not rewritten: its BlockId still resolves to the SAME
        // pre-move offset, and GetEmail round-trips the identical raw bytes through it.
        Assert.Equal(contentOffsetBefore, Locate(mgr, added.Value.ContentBlockId!).Offset);
        var read = mgr.GetEmail(added.Value.EmailId);
        Ok(read);
        Assert.True(read.Value.Found);
        Assert.Equal(mime, read.Value.Content);
    }

    // --------------------------------------------------------- Delete

    [Fact]
    public void Delete_removes_from_listings_and_indexes_and_counts_bytes_dead()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Delete", "body to delete completely");
        long ticks = 700700;

        var added = mgr.AddEmail(Request(folder, mime, ticks));
        Ok(added);
        folder = added.Value.Folder!;

        // Expected dead bytes = the Tier 3 content block + the Tier 2 metadata block on-disk lengths.
        long expectedDead =
            Locate(mgr, added.Value.ContentBlockId!).TotalBlockLength
            + Locate(mgr, added.Value.MetadataBlockId!).TotalBlockLength;
        long deadBefore = mgr.Accountant!.DeadByteCount;

        var deleted = mgr.DeleteEmail(new DeleteEmailRequest
        {
            Folder = folder,
            EmailId = added.Value.EmailId,
            DateTicks = ticks,
        });
        Ok(deleted);
        Assert.True(deleted.Value.WasPresent);
        Assert.True(deleted.Value.DateEntryRemoved);

        // Folder membership removed: the new delta head is a Delete for the email.
        Assert.Equal(folder.FolderVersion + 1, deleted.Value.Folder.FolderVersion);
        var entry = HeadDelta(mgr, deleted.Value.DeltaBlockId!);
        Assert.Equal(FolderDeltaOp.Delete, entry.Op);
        Assert.Equal(added.Value.EmailId, entry.EmailHashedId);

        // Index entries gone: the email reads as not-found, is absent from the primary index, and its
        // date entry no longer range-queries (read-your-writes over the buffered delete).
        var lookup = mgr.PrimaryIndex!.TryGet(added.Value.EmailId.GetBytes());
        Ok(lookup);
        Assert.False(lookup.Value.Found);
        var read = mgr.GetEmail(added.Value.EmailId);
        Ok(read);
        Assert.False(read.Value.Found);
        var range = mgr.DateIndex!.RangeQuery(ticks, ticks);
        Ok(range);
        Assert.Empty(range.Value);

        // Dead-byte accounting: exactly the content + metadata bytes moved live → dead.
        Assert.Equal(expectedDead, deleted.Value.DeadBytes);
        Assert.Equal(deadBefore + expectedDead, mgr.Accountant!.DeadByteCount);
    }

    [Fact]
    public void Delete_survives_reopen_and_bytes_stay_dead_in_the_checkpoint()
    {
        EmailManager.Create(_path).Value.Dispose();

        EmailDB.Format.V3.EmailHashedID id;
        long expectedDead;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            byte[] mime = Mime("DeleteReopen", "committed then deleted then reopened");
            long ticks = 555000;
            var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
            Ok(added);
            id = added.Value.EmailId;

            var deleted = mgr.DeleteEmail(new DeleteEmailRequest
            {
                Folder = added.Value.Folder!,
                EmailId = id,
                DateTicks = ticks,
            });
            Ok(deleted);
            expectedDead = deleted.Value.DeadBytes;
            Assert.True(expectedDead > 0);
            Assert.True(mgr.Close().IsSuccess);
        }

        // Reopen: the committed delete is durable — the identity is gone and the dead bytes the delete
        // accounted are restored from the Checkpoint (docs/Compaction.md Section 3).
        using var reopened = EmailManager.Open(_path).Value;
        var lookup = reopened.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.False(lookup.Value.Found);
        Assert.Equal(expectedDead, reopened.Accountant!.DeadByteCount);
    }

    [Fact]
    public void Delete_of_an_unknown_identity_is_a_clean_no_op()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        long deadBefore = mgr.Accountant!.DeadByteCount;

        var deleted = mgr.DeleteEmail(new DeleteEmailRequest
        {
            Folder = folder,
            EmailId = EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(Mime("Never", "was added")),
            DateTicks = 1,
        });
        Ok(deleted);

        Assert.False(deleted.Value.WasPresent);
        Assert.Null(deleted.Value.DeltaBlockId);
        Assert.Equal(0, deleted.Value.DeadBytes);
        // Nothing was written: the directory is unchanged and no bytes went dead.
        Assert.Equal(folder.FolderVersion, deleted.Value.Folder.FolderVersion);
        Assert.Equal(deadBefore, mgr.Accountant!.DeadByteCount);
    }

    [Fact]
    public void Delete_records_the_content_and_metadata_block_ids_in_a_cleanup_block()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("DeleteAudit", "delete must leave a durable audit record");
        long ticks = 616161;
        var added = mgr.AddEmail(Request(folder, mime, ticks));
        Ok(added);
        folder = added.Value.Folder!;

        var contentLoc = Locate(mgr, added.Value.ContentBlockId!);
        var metaLoc = Locate(mgr, added.Value.MetadataBlockId!);

        var deleted = mgr.DeleteEmail(new DeleteEmailRequest
        {
            Folder = folder,
            EmailId = added.Value.EmailId,
            DateTicks = ticks,
        });
        Ok(deleted);

        // Forward-scan the file (type-3 discovery, no index) and collect every Cleanup block's
        // recorded supersession entries. The delete's retirement must be a durable audit record.
        var scan = mgr.BlockManager.ScanForward();
        Ok(scan);
        Assert.Empty(scan.Value.DamagedRanges);

        var recorded = new List<SupersededBlockRecord>();
        int cleanupBlocks = 0;
        foreach (var loc in scan.Value.Blocks)
        {
            var read = mgr.BlockManager.Read(loc.Offset);
            Ok(read);
            if (read.Value.Header.Type != BlockType.Cleanup)
                continue;
            cleanupBlocks++;
            var cleanup = CleanupSerializer.Deserialize(read.Value.Payload);
            Ok(cleanup);
            recorded.AddRange(cleanup.Value.SupersededBlocks);
        }

        // Exactly one Cleanup block, naming the content block then the metadata block with their
        // exact on-disk sizes — the two blocks whose bytes the delete moved live → dead.
        Assert.Equal(1, cleanupBlocks);
        Assert.Equal(2, recorded.Count);
        Assert.Equal(added.Value.ContentBlockId, recorded[0].BlockId);
        Assert.Equal(contentLoc.TotalBlockLength, recorded[0].TotalBlockLength);
        Assert.Equal(added.Value.MetadataBlockId, recorded[1].BlockId);
        Assert.Equal(metaLoc.TotalBlockLength, recorded[1].TotalBlockLength);
        // The audit total equals the reported dead bytes.
        Assert.Equal(deleted.Value.DeadBytes, recorded.Sum(r => r.TotalBlockLength));
    }

    [Fact]
    public void Delete_is_absent_from_public_listing_indexes_and_content_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] folderId;
        EmailDB.Format.V3.EmailHashedID id;
        long ticks = 480480;
        long expectedDead;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            byte[] mime = Mime("DeleteListing", "committed, deleted, then listed after reopen");
            var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
            Ok(added);
            id = added.Value.EmailId;

            // Same session: the public ListFolder listing shows the row before the delete ...
            var before = mgr.ListFolder(added.Value.Folder!.FolderId, 0, 100);
            Ok(before);
            Assert.Equal(id, Assert.Single(before.Value.Records).EmailHashedId);

            var deleted = mgr.DeleteEmail(new DeleteEmailRequest
            {
                Folder = added.Value.Folder!,
                EmailId = id,
                DateTicks = ticks,
            });
            Ok(deleted);
            folderId = deleted.Value.Folder.FolderId;
            expectedDead = deleted.Value.DeadBytes;

            // ... and is gone from the public listing immediately after the delete (same session).
            var after = mgr.ListFolder(folderId, 0, 100);
            Ok(after);
            Assert.Empty(after.Value.Records);
            Assert.Equal(0, after.Value.TotalCount);

            Assert.True(mgr.Close().IsSuccess);
        }

        // Reopen: the committed delete is durable across every public surface — the row is gone from
        // the folder listing, the primary index lookup is not-found, the date-range query excludes it,
        // GetEmail is a clean not-found, and the dead bytes were restored from the Checkpoint.
        using var reopened = EmailManager.Open(_path).Value;

        var listing = reopened.ListFolder(folderId, 0, 100);
        Ok(listing);
        Assert.Empty(listing.Value.Records);
        Assert.Equal(0, listing.Value.TotalCount);

        var lookup = reopened.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.False(lookup.Value.Found);

        var range = reopened.DateIndex!.RangeQuery(ticks, ticks);
        Ok(range);
        Assert.Empty(range.Value);

        var read = reopened.GetEmail(id);
        Ok(read);
        Assert.False(read.Value.Found);

        Assert.Equal(expectedDead, reopened.Accountant!.DeadByteCount);
    }

    [Fact]
    public void Delete_of_one_email_leaves_the_others_fully_intact()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        // Three emails in one folder; the middle one is deleted, the other two must survive whole.
        var folder = NewFolder();
        byte[] mimeA = Mime("keepA", "oldest survivor content");
        byte[] mimeB = Mime("dropB", "the doomed middle email");
        byte[] mimeC = Mime("keepC", "newest survivor content");
        var a = mgr.AddEmail(Request(folder, mimeA, 100)); Ok(a); folder = a.Value.Folder!;
        var b = mgr.AddEmail(Request(folder, mimeB, 200)); Ok(b); folder = b.Value.Folder!;
        var c = mgr.AddEmail(Request(folder, mimeC, 300)); Ok(c); folder = c.Value.Folder!;

        var deleted = mgr.DeleteEmail(new DeleteEmailRequest
        {
            Folder = folder,
            EmailId = b.Value.EmailId,
            DateTicks = 200,
        });
        Ok(deleted);
        folder = deleted.Value.Folder;

        // Listing: exactly the two survivors, still date-descending; the deleted one is masked.
        var listing = mgr.ListFolder(folder.FolderId, 0, 100);
        Ok(listing);
        Assert.Equal(2, listing.Value.TotalCount);
        Assert.Equal(new long[] { 300, 100 }, listing.Value.Records.Select(r => r.DateTicks).ToArray());
        Assert.DoesNotContain(listing.Value.Records, r => r.EmailHashedId == b.Value.EmailId);

        // Both survivors keep their whole identity: primary-index dedupe hit → their own ContentBlockId,
        // date-range membership, and byte-identical content through GetEmail. The deleted one is gone
        // from every one of those surfaces.
        foreach (var (added, mime, ticks) in new[]
        {
            (a.Value, mimeA, 100L),
            (c.Value, mimeC, 300L),
        })
        {
            var lookup = mgr.PrimaryIndex!.TryGet(added.EmailId.GetBytes());
            Ok(lookup);
            Assert.True(lookup.Value.Found);
            Assert.Equal(added.ContentBlockId, lookup.Value.Value);

            var range = mgr.DateIndex!.RangeQuery(ticks, ticks);
            Ok(range);
            Assert.Equal(added.ContentBlockId, Assert.Single(range.Value).BlockId);

            var read = mgr.GetEmail(added.EmailId);
            Ok(read);
            Assert.True(read.Value.Found);
            Assert.Equal(mime, read.Value.Content);
        }

        var goneLookup = mgr.PrimaryIndex!.TryGet(b.Value.EmailId.GetBytes());
        Ok(goneLookup);
        Assert.False(goneLookup.Value.Found);
        var goneRange = mgr.DateIndex!.RangeQuery(200, 200);
        Ok(goneRange);
        Assert.Empty(goneRange.Value);
        var goneRead = mgr.GetEmail(b.Value.EmailId);
        Ok(goneRead);
        Assert.False(goneRead.Value.Found);
    }

    [Fact]
    public void Delete_of_a_row_in_a_committed_compiled_page_masks_it_across_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] mime = Mime("compiledDelete", "row lives in a compiled page, committed, then deleted");
        long ticks = 400;
        FolderPageDirectory source;
        EmailDB.Format.V3.EmailHashedID id;
        byte[] keeperId;

        // Session 1: a real email (so it lands in the primary index and has content+metadata blocks)
        // whose listing row sits in a COMPILED FolderPage alongside a keeper row. Commit it all.
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), mime, ticks));
            Ok(added);
            id = added.Value.EmailId;

            var doomedRec = RecordOf(added.Value, ticks, mime);
            var keeperRec = new ListingRecord
            {
                EmailHashedId = EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(Mime("keeper", "stays put")),
                ContentBlockId = new UlidGenerator().Next(),
                DateTicks = 500,
                Flags = ListingFlags.None,
                MessageSize = 123,
                From = "keeper@example.com",
                Subject = "Keeper",
                Preview = "keeper preview",
            };
            keeperId = keeperRec.EmailHashedId.GetBytes();

            var page = FolderPage.FromRecords(new[] { keeperRec, doomedRec });
            var pageLoc = mgr.Folders!.WritePage(page);
            Ok(pageLoc);
            source = FolderPageDirectory.Create(
                new UlidGenerator().Next(),
                new[] { new PageEntry(pageLoc.Value.BlockId, dateFrom: 400, dateTo: 500, entryCount: page.Count) });
            Ok(mgr.FolderDirectory!.WriteDirectory(source));
            Assert.True(mgr.Close().IsSuccess);
        }

        byte[] folderId = source.FolderId;

        // Session 2: the row predates this session (committed compiled page). DeleteEmail chains a
        // Delete delta over the committed page; the listing must mask the compiled row at once.
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var before = mgr.ListFolder(folderId, 0, 100);
            Ok(before);
            Assert.Equal(2, before.Value.TotalCount); // keeper + doomed row, from the compiled page

            var deleted = mgr.DeleteEmail(new DeleteEmailRequest
            {
                Folder = source,
                EmailId = id,
                DateTicks = ticks,
            });
            Ok(deleted);
            Assert.True(deleted.Value.WasPresent);

            var after = mgr.ListFolder(folderId, 0, 100);
            Ok(after);
            var kept = Assert.Single(after.Value.Records); // only the keeper remains
            Assert.Equal(keeperId, kept.EmailHashedId.GetBytes());
            var read = mgr.GetEmail(id);
            Ok(read);
            Assert.False(read.Value.Found);
            Assert.True(mgr.Close().IsSuccess);
        }

        // Session 3: the committed Delete delta still masks the compiled page row after reopen — the
        // keeper survives, the deleted identity is gone from the listing and reads not-found.
        using var reopened = EmailManager.Open(_path).Value;
        var listing = reopened.ListFolder(folderId, 0, 100);
        Ok(listing);
        var survivor = Assert.Single(listing.Value.Records);
        Assert.Equal(keeperId, survivor.EmailHashedId.GetBytes());

        var lookup = reopened.PrimaryIndex!.TryGet(id.GetBytes());
        Ok(lookup);
        Assert.False(lookup.Value.Found);
        var readAgain = reopened.GetEmail(id);
        Ok(readAgain);
        Assert.False(readAgain.Value.Found);
    }

    // --------------------------------------------------------- Flag change

    [Fact]
    public void ChangeFlags_appends_a_flagchange_delta_with_the_new_flags()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var folder = NewFolder();
        byte[] mime = Mime("Flag", "body whose flags change");
        var added = mgr.AddEmail(Request(folder, mime, 321000));
        Ok(added);
        folder = added.Value.Folder!;

        var newFlags = ListingFlags.Read | ListingFlags.Flagged | ListingFlags.Answered;
        var changed = mgr.ChangeFlags(folder, added.Value.EmailId, newFlags);
        Ok(changed);

        Assert.Equal(folder.FolderVersion + 1, changed.Value.Folder.FolderVersion);
        Assert.True(changed.Value.Folder.HasPendingDelta);
        Assert.Equal(changed.Value.DeltaBlockId, changed.Value.Folder.HeadDeltaBlockId);

        // The new delta head is a FlagChange carrying the email id and the exact new flags.
        var entry = HeadDelta(mgr, changed.Value.DeltaBlockId);
        Assert.Equal(FolderDeltaOp.FlagChange, entry.Op);
        Assert.Equal(added.Value.EmailId, entry.EmailHashedId);
        Assert.Equal(newFlags, entry.Flags);
    }

    // --------------------------------------------------------- Guards

    [Fact]
    public void Mailbox_operations_on_a_created_but_unopened_manager_fail()
    {
        using var created = EmailManager.Create(_path).Value; // Create, never Open
        var folder = NewFolder();
        var id = EmailDB.Format.V3.EmailHashedID.ComputeFromRawContent(Mime("x", "y"));

        Assert.True(created.ChangeFlags(folder, id, ListingFlags.Flagged).IsFailure);
        Assert.True(created.DeleteEmail(new DeleteEmailRequest { Folder = folder, EmailId = id, DateTicks = 1 }).IsFailure);
        Assert.True(created.MoveEmail(new MoveEmailRequest
        {
            SourceFolder = folder,
            TargetFolder = NewFolder(),
            Record = new ListingRecord { EmailHashedId = id, ContentBlockId = new UlidGenerator().Next() },
        }).IsFailure);
    }
}
