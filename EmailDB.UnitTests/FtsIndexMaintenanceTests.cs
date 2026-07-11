using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
// Disambiguate from the test project's own Models.EmailHashedID.
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Direct unit tests for <see cref="FtsIndex"/> and <see cref="FtsBlockStore"/> (US-EMDB-91-7):
/// ingest folds an email's From/To/Cc trigrams into postings, delete masks an email via a tombstone,
/// and <see cref="FtsIndex.Flush"/> writes an immutable segment + <see cref="FtsSearchRoot"/> that
/// <see cref="FtsIndex.Reconstruct"/> reads back — the segment-growth and reopen contract, exercised
/// over a bare block manager (plaintext) whose runtime offset map is the resolver.
/// </summary>
public class FtsIndexUnitTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"emaildb-ftsu-{Guid.NewGuid():N}.emdb");
    private readonly RuntimeBlockOffsetMap _offsetMap = new();
    private readonly BlockManager _manager;

    public FtsIndexUnitTests()
    {
        var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        _manager = new BlockManager(stream, offsetMap: _offsetMap, firstBlockOffset: 0, ownsStream: true);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FtsBlockStore Store() => new(_manager, provider: null, resolver: _offsetMap);
    private static V3Id Id(byte seed) =>
        new(Enumerable.Range(0, V3Id.Size).Select(i => (byte)(seed + i)).ToArray());
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static ResolvedRoot AsResolved(BlockLocation loc) => new()
    {
        BlockId = loc.BlockId,
        Offset = loc.Offset,
        TotalBlockLength = loc.TotalBlockLength,
        HintWasStale = false,
    };

    // --------------------------------------------------------- Ingest

    [Fact]
    public void AddEmail_makes_the_addresss_trigrams_queryable_with_the_From_field()
    {
        var index = new FtsIndex();
        index.AddEmail(Id(1), "ryan@biztactix.com.au", to: null, cc: null);

        // The doc example: "rya" resolves to the indexed email, tagged From.
        var rya = index.GetPostings(new Trigram('r', 'y', 'a'));
        Assert.Single(rya);
        Assert.Equal(Id(1), rya[0].Email);
        Assert.Equal(AddressField.From, rya[0].Fields);

        // Whole-address windowing: a trigram spanning the '@' boundary is present too.
        Assert.Single(index.GetPostings(new Trigram('n', '@', 'b')));
        Assert.True(index.ContainsEmail(Id(1)));

        // A missing trigram yields nothing.
        Assert.Empty(index.GetPostings(new Trigram('z', 'z', 'z')));
    }

    [Fact]
    public void Field_flags_distinguish_and_OR_merge_From_To_Cc()
    {
        var index = new FtsIndex();
        // From and To are the same address, so their shared trigrams OR to From|To; Cc is distinct.
        index.AddEmail(Id(1), from: "a@shared.com", to: new[] { "a@shared.com" }, cc: new[] { "c@other.com" });

        var sha = index.GetPostings(new Trigram('s', 'h', 'a'));
        Assert.Single(sha);
        Assert.Equal(AddressField.From | AddressField.To, sha[0].Fields);

        var oth = index.GetPostings(new Trigram('o', 't', 'h'));
        Assert.Single(oth);
        Assert.Equal(AddressField.Cc, oth[0].Fields);
    }

    [Fact]
    public void An_address_shorter_than_a_trigram_indexes_nothing()
    {
        var index = new FtsIndex();
        index.AddEmail(Id(1), from: "ab", to: null, cc: null);
        Assert.False(index.ContainsEmail(Id(1)));
        Assert.False(index.IsDirty);
    }

    // --------------------------------------------------------- Delete

    [Fact]
    public void RemoveEmail_masks_the_email_but_leaves_the_others_at_a_shared_trigram()
    {
        var index = new FtsIndex();
        index.AddEmail(Id(1), "ryan@x.com", null, null);   // rya, yan, ...
        index.AddEmail(Id(2), "bryan@y.com", null, null);  // bry, rya, yan, ...

        var before = index.GetPostings(new Trigram('r', 'y', 'a'));
        Assert.Equal(2, before.Count);

        Assert.True(index.RemoveEmail(Id(1)));

        var after = index.GetPostings(new Trigram('r', 'y', 'a'));
        Assert.Single(after);
        Assert.Equal(Id(2), after[0].Email);
        Assert.False(index.ContainsEmail(Id(1)));

        // Deleting an email the index does not hold is a no-op.
        Assert.False(index.RemoveEmail(Id(1)));
    }

    // --------------------------------------------------------- Segment flush + reconstruct

    [Fact]
    public void Flush_writes_a_segment_that_Reconstruct_reads_back()
    {
        var store = Store();
        var index = new FtsIndex();
        index.AddEmail(Id(1), "ryan@biztactix.com.au", null, null);
        index.AddEmail(Id(2), "alice@example.org", null, null);

        var flushed = index.Flush(store);
        Ok(flushed);
        Assert.NotNull(flushed.Value);
        Assert.False(index.IsDirty);

        var reopened = FtsIndex.Reconstruct(store, AsResolved(flushed.Value!));
        Ok(reopened);
        var recovered = reopened.Value;

        Assert.True(recovered.ContainsEmail(Id(1)));
        Assert.True(recovered.ContainsEmail(Id(2)));
        Assert.Equal(index.IndexedEmailCount, recovered.IndexedEmailCount);
        var rya = recovered.GetPostings(new Trigram('r', 'y', 'a'));
        Assert.Single(rya);
        Assert.Equal(Id(1), rya[0].Email);
    }

    [Fact]
    public void Flush_after_a_delete_omits_the_deleted_email_from_the_new_segment()
    {
        var store = Store();
        var index = new FtsIndex();
        index.AddEmail(Id(1), "ryan@x.com", null, null);
        index.AddEmail(Id(2), "bryan@y.com", null, null);
        Ok(index.Flush(store)); // segment 0 holds both

        Assert.True(index.RemoveEmail(Id(1)));
        var flushed = index.Flush(store); // segment 1 must omit Id(1)
        Ok(flushed);

        var reopened = FtsIndex.Reconstruct(store, AsResolved(flushed.Value!)).Value;
        Assert.False(reopened.ContainsEmail(Id(1)));
        Assert.True(reopened.ContainsEmail(Id(2)));
        var rya = reopened.GetPostings(new Trigram('r', 'y', 'a'));
        Assert.Single(rya);
        Assert.Equal(Id(2), rya[0].Email);
    }

    [Fact]
    public void Reflushing_a_clean_index_returns_the_same_root_without_writing()
    {
        var store = Store();
        var index = new FtsIndex();
        index.AddEmail(Id(1), "ryan@x.com", null, null);

        var first = index.Flush(store);
        Ok(first);

        var second = index.Flush(store); // clean: re-registers the existing root, writes nothing
        Ok(second);
        Assert.False(index.IsDirty);
        Assert.Equal(first.Value!.Offset, second.Value!.Offset);
        Assert.Equal(first.Value!.BlockId, second.Value!.BlockId);
    }

    [Fact]
    public void An_empty_index_flush_has_no_root_to_register()
    {
        var flushed = new FtsIndex().Flush(Store());
        Ok(flushed);
        Assert.Null(flushed.Value);
    }
}

/// <summary>
/// Integration tests for FTS index maintenance wired into the v3 <see cref="EmailManager"/>
/// AddEmail/DeleteEmail pipeline (US-EMDB-91-7): each ingest folds addresses into the session index,
/// each delete masks the email, and a commit flushes the segment and registers the
/// <see cref="FtsSearchRoot"/> under <see cref="BTreeIndexKind.Fts"/> (IndexKind 3) so the index — and
/// its deletes — survive close/reopen, both plaintext and encrypted.
/// </summary>
public class FtsIndexMaintenanceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"emaildb-ftsm-{Guid.NewGuid():N}.emdb");
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Mime(string tag) =>
        Encoding.UTF8.GetBytes($"From: x\r\nSubject: {tag}\r\n\r\nbody-{tag}");

    private static FolderPageDirectory NewFolder() =>
        FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());

    private static AddEmailRequest Request(
        FolderPageDirectory folder, byte[] mime, long ticks, string from,
        string[]? to = null, string[]? cc = null) => new()
    {
        RawContent = mime,
        Folder = folder,
        MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
        DateTicks = ticks,
        Flags = ListingFlags.Read,
        From = from,
        To = to ?? Array.Empty<string>(),
        Cc = cc ?? Array.Empty<string>(),
        Subject = "probe",
        Preview = "preview",
    };

    // --------------------------------------------------------- Session ingest

    [Fact]
    public void AddEmail_folds_From_To_Cc_into_the_session_Fts_index()
    {
        EmailManager.Create(_path).Value.Dispose();
        using var mgr = EmailManager.Open(_path).Value;

        var added = mgr.AddEmail(Request(
            NewFolder(), Mime("a"), 1000,
            from: "ryan@biztactix.com.au",
            to: new[] { "alice@example.org" },
            cc: new[] { "carol@cc.net" }));
        Ok(added);

        var fts = mgr.Fts!;
        Assert.True(fts.ContainsEmail(added.Value.EmailId));

        var rya = fts.GetPostings(new Trigram('r', 'y', 'a'));  // From
        Assert.Single(rya);
        Assert.Equal(AddressField.From, rya[0].Fields);

        Assert.Single(fts.GetPostings(new Trigram('l', 'i', 'c'))); // "alice" — To
        Assert.Equal(AddressField.To, fts.GetPostings(new Trigram('l', 'i', 'c'))[0].Fields);
        Assert.Equal(AddressField.Cc, fts.GetPostings(new Trigram('c', 'a', 'r'))[0].Fields); // "carol" — Cc
    }

    // --------------------------------------------------------- Reopen

    [Fact]
    public void Committed_Fts_postings_survive_close_and_reopen_and_register_under_IndexKind_3()
    {
        EmailManager.Create(_path).Value.Dispose();
        V3Id id;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("keep"), 2000, from: "ryan@biztactix.com.au"));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close()); // Close commits: flush the segment + register the root.
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            // The reopened index was rebuilt from the Checkpoint's IndexKind-3 secondary entry.
            Assert.Contains(reopened.Checkpoint!.SecondaryIndexes, s => s.IndexKind == BTreeIndexKind.Fts);

            var rya = reopened.Fts!.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
            Assert.Equal(id, rya[0].Email);
            Assert.True(reopened.Fts.ContainsEmail(id));
        }
    }

    [Fact]
    public void A_deleted_email_is_excluded_from_the_Fts_index_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();
        V3Id kept, deleted;
        var folder = NewFolder();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var a = mgr.AddEmail(Request(folder, Mime("del"), 3000, from: "ryan@x.com"));   // rya
            Ok(a);
            var b = mgr.AddEmail(Request(a.Value.Folder!, Mime("keep"), 3001, from: "bryan@y.com")); // rya
            Ok(b);
            deleted = a.Value.EmailId;
            kept = b.Value.EmailId;
            Ok(mgr.Commit());

            var del = mgr.DeleteEmail(new DeleteEmailRequest
            {
                Folder = b.Value.Folder!,
                EmailId = deleted,
                DateTicks = 3000,
            });
            Ok(del);
            Assert.True(del.Value.WasPresent);
            // Masked immediately in-session.
            Assert.False(mgr.Fts!.ContainsEmail(deleted));
            Ok(mgr.Close());
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            var rya = reopened.Fts!.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
            Assert.Equal(kept, rya[0].Email);
            Assert.False(reopened.Fts.ContainsEmail(deleted));
        }
    }

    [Fact]
    public void Deleting_an_email_masks_its_To_and_Cc_trigrams_and_a_shared_recipient_survives_reopen()
    {
        // Two emails share a To recipient (team@shared.com) and a Cc recipient (watch@shared.com), so the
        // 'tea' (To) and 'wat' (Cc) posting lists each hold both emails. Deleting one must leave the other's
        // To/Cc postings intact — the delete path must reach the To and Cc fields, not just From.
        EmailManager.Create(_path).Value.Dispose();
        V3Id kept, deleted;
        var folder = NewFolder();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var a = mgr.AddEmail(Request(folder, Mime("del"), 6000,
                from: "ryan@from.com", to: new[] { "team@shared.com" }, cc: new[] { "watch@shared.com" }));
            Ok(a);
            var b = mgr.AddEmail(Request(a.Value.Folder!, Mime("keep"), 6001,
                from: "bryan@from.com", to: new[] { "team@shared.com" }, cc: new[] { "watch@shared.com" }));
            Ok(b);
            deleted = a.Value.EmailId;
            kept = b.Value.EmailId;
            Ok(mgr.Commit()); // committed segment holds both emails' To/Cc postings

            var tea = mgr.Fts!.GetPostings(new Trigram('t', 'e', 'a')); // "team" — To
            Assert.Equal(2, tea.Count);
            var wat = mgr.Fts.GetPostings(new Trigram('w', 'a', 't')); // "watch" — Cc
            Assert.Equal(2, wat.Count);

            var del = mgr.DeleteEmail(new DeleteEmailRequest { Folder = b.Value.Folder!, EmailId = deleted, DateTicks = 6000 });
            Ok(del);
            Assert.True(del.Value.WasPresent);

            // Masked immediately in-session: the To and Cc posting lists now name only the kept email.
            Assert.False(mgr.Fts.ContainsEmail(deleted));
            var teaAfter = mgr.Fts.GetPostings(new Trigram('t', 'e', 'a'));
            Assert.Single(teaAfter);
            Assert.Equal(kept, teaAfter[0].Email);
            Assert.Equal(AddressField.To, teaAfter[0].Fields);
            var watAfter = mgr.Fts.GetPostings(new Trigram('w', 'a', 't'));
            Assert.Single(watAfter);
            Assert.Equal(kept, watAfter[0].Email);
            Assert.Equal(AddressField.Cc, watAfter[0].Fields);
            Ok(mgr.Close()); // flush purges the tombstoned email from the new segment
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            // After a commit checkpoint + reopen the deletion is durable by omission for To and Cc alike.
            Assert.False(reopened.Fts!.ContainsEmail(deleted));
            Assert.True(reopened.Fts.ContainsEmail(kept));
            var tea = reopened.Fts.GetPostings(new Trigram('t', 'e', 'a'));
            Assert.Single(tea);
            Assert.Equal(kept, tea[0].Email);
            Assert.Equal(AddressField.To, tea[0].Fields);
            var wat = reopened.Fts.GetPostings(new Trigram('w', 'a', 't'));
            Assert.Single(wat);
            Assert.Equal(kept, wat[0].Email);
            Assert.Equal(AddressField.Cc, wat[0].Fields);
        }
    }

    [Fact]
    public void Deleting_an_email_the_Fts_index_never_held_is_a_clean_no_op()
    {
        // An email whose only address is shorter than a trigram is stored (it lives in the primary index)
        // but contributes no FTS posting. Deleting it is a present-and-successful delete that the FTS index
        // treats as a no-op — no throw, no disturbance to another email's live postings.
        EmailManager.Create(_path).Value.Dispose();
        var folder = NewFolder();
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var unindexed = mgr.AddEmail(Request(folder, Mime("short"), 7000, from: "ab")); // "ab" < 3 chars
            Ok(unindexed);
            var indexed = mgr.AddEmail(Request(unindexed.Value.Folder!, Mime("real"), 7001, from: "ryan@x.com"));
            Ok(indexed);

            Assert.False(mgr.Fts!.ContainsEmail(unindexed.Value.EmailId)); // never indexed
            Assert.True(mgr.Fts.ContainsEmail(indexed.Value.EmailId));

            var del = mgr.DeleteEmail(new DeleteEmailRequest
            {
                Folder = indexed.Value.Folder!,
                EmailId = unindexed.Value.EmailId,
                DateTicks = 7000,
            });
            Ok(del);
            Assert.True(del.Value.WasPresent); // the email existed and was deleted

            // The clean no-op left the other email's posting untouched.
            var rya = mgr.Fts.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
            Assert.Equal(indexed.Value.EmailId, rya[0].Email);
            Ok(mgr.Close());
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            var rya = reopened.Fts!.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
        }
    }

    // --------------------------------------------------------- Encryption

    [Fact]
    public void Fts_blocks_are_encrypted_on_an_encrypted_file_and_recover_on_reopen()
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions { Password = pw, KdfParameters = FastParams })
            .Value.Dispose();

        V3Id id;
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            Assert.True(mgr.IsEncrypted);
            var added = mgr.AddEmail(Request(NewFolder(), Mime("secret"), 4000, from: "ryan@biztactix.com.au"));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close());
        }

        using (var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            // The FTSSearchRoot block on disk is ciphertext (trigrams reverse to addresses, spec 9.5).
            var fts = reopened.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Fts);
            var rootBlock = reopened.BlockManager.Read(fts.Root.Offset);
            Ok(rootBlock);
            Assert.Equal(BlockType.FTSSearchRoot, rootBlock.Value.Header.Type);
            Assert.True(rootBlock.Value.Header.IsEncrypted);

            // And decryption on reopen recovers the postings.
            var rya = reopened.Fts!.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
            Assert.Equal(id, rya[0].Email);
        }
    }

    // A deliberately rare address: its 'z','q','x' trigram and the resulting email id are
    // markers we scan the on-disk FTS blocks for. In a plaintext posting they appear verbatim;
    // in ciphertext they must not, so their absence proves the payload is genuinely encrypted.
    private const string RareAddress = "zqxjw@zqxjw.example";

    /// <summary>
    /// Story acceptance criterion — "FTS blocks are encrypted under every policy": for an
    /// encrypted file created under each <see cref="EncryptionPolicy"/> the v3 stack defines
    /// (<see cref="EncryptionPolicy.Default"/> and <see cref="EncryptionPolicy.Full"/>), every FTS
    /// block a real ingest writes — all four types 14-17 (<see cref="BlockType.FTSSegmentMeta"/>,
    /// <see cref="BlockType.FTSTermDictionary"/>, <see cref="BlockType.FTSPostingList"/>,
    /// <see cref="BlockType.FTSSearchRoot"/>) — lands on disk encrypted: header <c>Encrypted</c>
    /// flag set, active key epoch stamped, and the ciphertext payload leaks neither the indexed
    /// trigram nor the email id that a plaintext posting would expose (spec Section 9.5).
    /// </summary>
    [Theory]
    [InlineData(EncryptionPolicy.Default)]
    [InlineData(EncryptionPolicy.Full)]
    public void Fts_blocks_land_encrypted_on_disk_under_every_policy(EncryptionPolicy policy)
    {
        const string pw = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = pw,
            KdfParameters = FastParams,
            EncryptionPolicy = policy,
        }).Value.Dispose();

        byte[] emailIdBytes;
        using (var mgr = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            Assert.True(mgr.IsEncrypted);
            var added = mgr.AddEmail(Request(
                NewFolder(), Mime("secret"), 4000, from: RareAddress, to: new[] { RareAddress }));
            Ok(added);
            emailIdBytes = added.Value.EmailId.GetBytes();
            Ok(mgr.Close()); // Commit flushes the segment + registers the SearchRoot.
        }

        byte[] trigramBytes = new Trigram('z', 'q', 'x').GetBytes();

        using (var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = pw }).Value)
        {
            ushort activeEpoch = reopened.EncryptionProvider!.ActiveEpoch;
            var blocks = CollectFtsBlocks(reopened);

            // The ingest produced all four FTS block types (14-17).
            var types = blocks.Select(b => b.Header.Type).ToHashSet();
            Assert.Contains(BlockType.FTSSegmentMeta, types);
            Assert.Contains(BlockType.FTSTermDictionary, types);
            Assert.Contains(BlockType.FTSPostingList, types);
            Assert.Contains(BlockType.FTSSearchRoot, types);

            foreach (var b in blocks)
            {
                Assert.True(b.Header.IsEncrypted,
                    $"{b.Header.Type} must be encrypted at rest under {policy}.");
                Assert.Equal(activeEpoch, b.Header.KeyEpoch); // active key epoch stamped
                Assert.False(Contains(b.Payload, trigramBytes),
                    $"{b.Header.Type} ciphertext leaked a plaintext trigram under {policy}.");
                Assert.False(Contains(b.Payload, emailIdBytes),
                    $"{b.Header.Type} ciphertext leaked a plaintext email id under {policy}.");
            }
        }

        // Whole-file (manager closed so the handle is free): the indexed address never appears in
        // the clear at rest — content and content-derived blocks are encrypted under both policies.
        var raw = File.ReadAllBytes(_path);
        Assert.False(Contains(raw, Encoding.UTF8.GetBytes(RareAddress)),
            $"The raw encrypted file leaked the indexed address under {policy}.");
    }

    /// <summary>
    /// Negative control for the "always encrypted" rule: docs/Search.md says FTS is always
    /// encrypted <i>under every policy</i>, but a plaintext file has no keys — so, like every
    /// other block, the FTS blocks are written in the clear (they are not skipped; the
    /// <see cref="FtsBlockStore"/> "no provider ⇒ plaintext" contract). The clear trigram and
    /// email id are therefore present on disk, confirming genuine plaintext.
    /// </summary>
    [Fact]
    public void Fts_blocks_are_plaintext_on_an_unencrypted_file()
    {
        EmailManager.Create(_path).Value.Dispose();

        byte[] emailIdBytes;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            Assert.False(mgr.IsEncrypted);
            var added = mgr.AddEmail(Request(NewFolder(), Mime("plain"), 5000, from: RareAddress));
            Ok(added);
            emailIdBytes = added.Value.EmailId.GetBytes();
            Ok(mgr.Close());
        }

        byte[] trigramBytes = new Trigram('z', 'q', 'x').GetBytes();

        using (var reopened = EmailManager.Open(_path).Value)
        {
            var blocks = CollectFtsBlocks(reopened);

            Assert.Contains(BlockType.FTSTermDictionary, blocks.Select(b => b.Header.Type));
            foreach (var b in blocks)
                Assert.False(b.Header.IsEncrypted, $"{b.Header.Type} should be plaintext on an unencrypted file.");

            // The clear trigram and email id sit on disk — proof the FTS blocks are truly plaintext.
            Assert.Contains(blocks, b =>
                b.Header.Type == BlockType.FTSTermDictionary && Contains(b.Payload, trigramBytes));
            Assert.Contains(blocks, b =>
                b.Header.Type == BlockType.FTSPostingList && Contains(b.Payload, emailIdBytes));
        }
    }

    // ------------------------------------------ Root recovery from the Checkpoint (US-EMDB-91-5)

    /// <summary>The checkpoint's registered FTS (IndexKind 3) root — its resolved on-disk location.</summary>
    private static ResolvedRoot FtsRoot(EmailManager mgr) =>
        mgr.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Fts).Root;

    /// <summary>Reads the monotonic SearchRootSequence of the FTSSearchRoot block at <paramref name="offset"/>.</summary>
    private static ulong SearchRootSequenceAt(EmailManager mgr, long offset)
    {
        var store = new FtsBlockStore(mgr.BlockManager, mgr.EncryptionProvider, mgr.Resolver);
        var root = store.ReadSearchRoot(offset);
        Ok(root);
        return root.Value.SearchRootSequence;
    }

    /// <summary>
    /// Story acceptance criterion — "Root recoverable from the Checkpoint secondary index table":
    /// after a commit, the checkpoint's generic secondary table holds exactly one IndexKind-3 entry
    /// whose pointer resolves to a real <see cref="BlockType.FTSSearchRoot"/> block (type 17), and a
    /// clean reopen serves postings straight from that recovered root — this manager calls no
    /// AddEmail, so a served posting can only have come from the checkpoint's secondary entry, not a
    /// re-ingest.
    /// </summary>
    [Fact]
    public void Committed_root_is_registered_at_a_real_search_root_block_and_a_clean_reopen_serves_postings_without_reingest()
    {
        EmailManager.Create(_path).Value.Dispose();
        V3Id id;
        using (var mgr = EmailManager.Open(_path).Value)
        {
            var added = mgr.AddEmail(Request(NewFolder(), Mime("root"), 8000, from: "ryan@biztactix.com.au"));
            Ok(added);
            id = added.Value.EmailId;
            Ok(mgr.Close()); // commit: flush the segment + register the root under IndexKind 3.
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            // (a) Exactly one IndexKind-3 entry, and it points at a genuine FTSSearchRoot block.
            var ftsEntries = reopened.Checkpoint!.SecondaryIndexes
                .Where(s => s.IndexKind == BTreeIndexKind.Fts).ToList();
            Assert.Single(ftsEntries);
            var rootBlock = reopened.BlockManager.Read(ftsEntries[0].Root.Offset);
            Ok(rootBlock);
            Assert.Equal(BlockType.FTSSearchRoot, rootBlock.Value.Header.Type);

            // (b) The reopened index serves postings without any re-ingest on this manager.
            Assert.True(reopened.Fts!.ContainsEmail(id));
            var rya = reopened.Fts.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
            Assert.Equal(id, rya[0].Email);
            Assert.Equal(AddressField.From, rya[0].Fields);
        }
    }

    /// <summary>
    /// Latest-checkpoint-wins: a second commit appends a NEW FTSSearchRoot that supersedes the first,
    /// and the checkpoint re-registers the newer root. On reopen the recovered root is the newer one
    /// (higher <see cref="FtsSearchRoot.SearchRootSequence"/>, different offset), and its index holds
    /// BOTH emails — proving the older root, which knew only the first email, is not what was recovered.
    /// </summary>
    [Fact]
    public void A_root_superseded_by_a_newer_commit_is_not_the_one_recovered()
    {
        EmailManager.Create(_path).Value.Dispose();
        V3Id first, second;
        long root1Offset;
        var folder = NewFolder();

        using (var mgr = EmailManager.Open(_path).Value)
        {
            var a = mgr.AddEmail(Request(folder, Mime("one"), 9000, from: "ryan@biztactix.com.au")); // rya
            Ok(a);
            first = a.Value.EmailId;
            folder = a.Value.Folder!;
            Ok(mgr.Close()); // commit 1: root R1 names a segment holding only `first`.
        }

        using (var afterFirst = EmailManager.Open(_path).Value)
        {
            root1Offset = FtsRoot(afterFirst).Offset;
            Assert.True(afterFirst.Fts!.ContainsEmail(first));
            Ok(afterFirst.Close());
        }

        using (var mgr = EmailManager.Open(_path).Value)
        {
            var b = mgr.AddEmail(Request(folder, Mime("two"), 9001, from: "brant@example.org")); // bra/ran
            Ok(b);
            second = b.Value.EmailId;
            Ok(mgr.Close()); // commit 2: root R2 supersedes R1, its segments hold BOTH emails.
        }

        using (var reopened = EmailManager.Open(_path).Value)
        {
            long root2Offset = FtsRoot(reopened).Offset;
            // The registered root advanced off R1 to a newer, higher-sequence R2.
            Assert.NotEqual(root1Offset, root2Offset);
            Assert.True(SearchRootSequenceAt(reopened, root2Offset) > SearchRootSequenceAt(reopened, root1Offset));

            // The recovered index is R2's: it holds BOTH emails. Had recovery loaded the superseded
            // R1 (which knew only `first`), `second` would be absent.
            Assert.True(reopened.Fts!.ContainsEmail(first));
            Assert.True(reopened.Fts.ContainsEmail(second));
            Assert.Single(reopened.Fts.GetPostings(new Trigram('b', 'r', 'a'))); // `brant` — only in R2
        }
    }

    /// <summary>
    /// Dirty-open path (kill -9 between operations, spec Section 10.2 step 4): after a durable commit
    /// registers root R1, a fresh session folds another email into the SESSION FTS index (in memory —
    /// its segment is only written at commit) and the process dies with no Close/Commit, leaving the
    /// superblock dirty. The recovery reopen must reconstruct the FTS index from the LAST DURABLE
    /// commit's registered root (R1) and NOT resurrect the uncommitted in-session FTS state.
    /// </summary>
    [Fact]
    public void Dirty_open_recovers_the_last_durable_root_and_does_not_resurrect_uncommitted_fts_state()
    {
        EmailManager.Create(_path).Value.Dispose();
        V3Id durable, uncommitted;
        long durableRootOffset;
        var folder = NewFolder();

        using (var mgr = EmailManager.Open(_path).Value)
        {
            var a = mgr.AddEmail(Request(folder, Mime("durable"), 10000, from: "ryan@biztactix.com.au")); // rya
            Ok(a);
            durable = a.Value.EmailId;
            folder = a.Value.Folder!;
            Ok(mgr.Close()); // durable commit: segment + FTSSearchRoot R1 registered.
        }
        using (var afterCommit = EmailManager.Open(_path).Value)
        {
            durableRootOffset = FtsRoot(afterCommit).Offset;
            Ok(afterCommit.Close());
        }

        // Kill -9: a new session ingests `uncommitted` into the session FTS index, then the process
        // dies (Dispose, no Close ⇒ no flush, no Checkpoint). The FTS segment for it is never written.
        {
            var mgr = EmailManager.Open(_path).Value;
            var b = mgr.AddEmail(Request(folder, Mime("ghost"), 10001, from: "brant@ghost.example")); // bra
            Ok(b);
            uncommitted = b.Value.EmailId;
            Assert.True(mgr.Fts!.ContainsEmail(uncommitted)); // present in-session, pre-crash
            mgr.Dispose(); // process death: no Close, no final Checkpoint.
        }
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sb = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sb.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0; // the crash flag.
            Ok(sb.Write(dirty));
        }

        // The reopen genuinely needs recovery: the clean fast path refuses the dirty file.
        using (var probeStream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var probe = CleanOpener.Open(probeStream);
            Ok(probe);
            Assert.Equal(OpenOutcomeKind.DirtyOpenRequired, probe.Value.Kind);
        }

        using (var recovered = EmailManager.Open(_path).Value)
        {
            Assert.Equal(1, recovered.Superblock.CleanShutdown); // healed by recovery, not the clean path
            // The FTS root recovered is R1 — the last DURABLE commit's registered root.
            Assert.Equal(durableRootOffset, FtsRoot(recovered).Offset);

            // Committed FTS state survived...
            Assert.True(recovered.Fts!.ContainsEmail(durable));
            var rya = recovered.Fts.GetPostings(new Trigram('r', 'y', 'a'));
            Assert.Single(rya);
            Assert.Equal(durable, rya[0].Email);

            // ...and the uncommitted in-session FTS state was NOT resurrected.
            Assert.False(recovered.Fts.ContainsEmail(uncommitted));
            Assert.Empty(recovered.Fts.GetPostings(new Trigram('b', 'r', 'a'))); // `brant` never committed
        }
    }

    /// <summary>
    /// Reads every on-disk FTS block (types 14-17) reachable from the checkpoint's registered
    /// <see cref="FtsSearchRoot"/>: root → segment metas → term dictionaries → posting lists.
    /// Each block is read raw through the <see cref="BlockManager"/> so its header flags and
    /// (possibly ciphertext) payload can be inspected as they sit on disk.
    /// </summary>
    private static List<Block> CollectFtsBlocks(EmailManager mgr)
    {
        var store = new FtsBlockStore(mgr.BlockManager, mgr.EncryptionProvider, mgr.Resolver);
        var ftsRoot = mgr.Checkpoint!.SecondaryIndexes.Single(s => s.IndexKind == BTreeIndexKind.Fts);

        var offsets = new List<long> { ftsRoot.Root.Offset };
        var root = store.ReadSearchRoot(ftsRoot.Root.Offset);
        Ok(root);
        foreach (var segId in root.Value.SegmentMetaBlockIds)
        {
            var segOff = store.ResolveOffset(segId);
            Ok(segOff);
            offsets.Add(segOff.Value);

            var meta = store.ReadSegmentMeta(segOff.Value);
            Ok(meta);
            var tdOff = store.ResolveOffset(meta.Value.TermDictionaryBlockId);
            Ok(tdOff);
            offsets.Add(tdOff.Value);

            var dict = store.ReadTermDictionary(tdOff.Value);
            Ok(dict);
            foreach (var entry in dict.Value.Entries)
            {
                var plOff = store.ResolveOffset(entry.PostingListBlockId);
                Ok(plOff);
                offsets.Add(plOff.Value);
            }
        }

        var blocks = new List<Block>();
        foreach (var off in offsets)
        {
            var read = mgr.BlockManager.Read(off);
            Ok(read);
            blocks.Add(read.Value);
        }
        return blocks;
    }

    /// <summary>True if <paramref name="needle"/> occurs as a contiguous byte run in <paramref name="haystack"/>.</summary>
    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;
}
