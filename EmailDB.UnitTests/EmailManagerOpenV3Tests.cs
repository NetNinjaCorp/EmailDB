using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="EmailManager.Open"/> (US-EMDB-84-6,
/// EmailDB_FileFormat_Spec.md Section 10.2): reopen an existing v3 file by running the
/// full open protocol and composing every layer — superblock select/validate,
/// encryption bootstrap, Checkpoint load, index construction, folder-manager wiring,
/// and WAL-replay recovery — into one ready <see cref="EmailManager"/> instance that
/// exposes the composed components and holds the writer lock.
///
/// <para>The decisive cases are: open-after-create (plaintext and encrypted) yields a
/// ready instance with every component wired; and opening a file that was left dirty
/// (a crash with <c>CleanShutdown = 0</c>) recovers via the bounded dirty scan back to
/// the last committed Checkpoint.</para>
/// </summary>
public class EmailManagerOpenV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-open-{Guid.NewGuid():N}.emdb");

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) keep the KDF fast in tests.
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static void AssertComponentsWired(EmailManager manager)
    {
        Assert.True(manager.IsOpen);
        Assert.NotNull(manager.BlockManager);
        Assert.NotNull(manager.Superblock);
        Assert.NotNull(manager.Checkpoint);
        Assert.NotNull(manager.LocationIndex);
        Assert.NotNull(manager.Resolver);
        Assert.NotNull(manager.NodeStore);
        Assert.NotNull(manager.Folders);
        Assert.NotNull(manager.FolderDirectory);
        Assert.NotNull(manager.FolderDeltas);
        // The superblock adopted on open is clean (fast path or healed by recovery).
        Assert.Equal(1, manager.Superblock.CleanShutdown);
    }

    // ------------------------------------------------------------- Unencrypted

    [Fact]
    public void Open_after_create_unencrypted_yields_a_ready_instance_with_all_components_wired()
    {
        EmailManager.Create(_path).Value.Dispose();

        var opened = EmailManager.Open(_path);
        Ok(opened);
        using var manager = opened.Value;

        AssertComponentsWired(manager);
        Assert.False(manager.IsEncrypted);
        Assert.Null(manager.EncryptionProvider);

        // The composed Checkpoint is the first one (sequence 0) with create's roots.
        Assert.Equal(0UL, manager.LastCheckpointSequence);
        Assert.Equal(0UL, manager.Checkpoint!.Checkpoint.CheckpointSequence);
        Assert.NotNull(manager.Checkpoint.MetadataRoot);
        Assert.NotNull(manager.Checkpoint.FolderTreeRoot);
        Assert.Null(manager.Checkpoint.KeyStoreRoot);

        // Empty index roots -> no live blocks in the reconstructed location index.
        Assert.Equal(0, (int)manager.LocationIndex!.Count);
    }

    [Fact]
    public void Open_holds_the_writer_lock_so_a_second_open_is_refused()
    {
        EmailManager.Create(_path).Value.Dispose();

        using var first = EmailManager.Open(_path).Value;
        var second = EmailManager.Open(_path);
        Assert.True(second.IsFailure, "a second open must be refused while the writer lock is held.");
    }

    [Fact]
    public void Open_of_a_missing_file_fails()
    {
        var opened = EmailManager.Open(_path);
        Assert.True(opened.IsFailure);
    }

    // --------------------------------------------------------------- Encrypted

    [Fact]
    public void Open_after_create_encrypted_with_the_password_yields_a_ready_instance()
    {
        const string password = "correct horse battery staple";
        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = password,
            KdfParameters = FastParams,
        }).Value.Dispose();

        var opened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password });
        Ok(opened);
        using var manager = opened.Value;

        AssertComponentsWired(manager);
        Assert.True(manager.IsEncrypted);
        Assert.NotNull(manager.EncryptionProvider);
        Assert.Equal(0, manager.EncryptionProvider!.ActiveEpoch);
        Assert.Equal(1, manager.Superblock.EncryptionEnabled);

        // The encrypted file's Checkpoint names a KeyStore root.
        Assert.NotNull(manager.Checkpoint!.KeyStoreRoot);
    }

    [Fact]
    public void Open_encrypted_without_a_password_reports_the_encryption_seam_as_a_failure()
    {
        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = "hunter2",
            KdfParameters = FastParams,
        }).Value.Dispose();

        // Opening an encrypted file with no password must fail, not read ciphertext.
        var opened = EmailManager.Open(_path);
        Assert.True(opened.IsFailure);
    }

    [Fact]
    public void Open_encrypted_with_the_wrong_password_fails()
    {
        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = "the-right-password",
            KdfParameters = FastParams,
        }).Value.Dispose();

        var opened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = "the-WRONG-password" });
        Assert.True(opened.IsFailure);
    }

    // ------------------------------------------------ Operable (usable-wiring) instance

    private static EmailDB.Format.V3.EmailHashedID IdOf(byte seed)
    {
        var raw = new byte[EmailDB.Format.V3.EmailHashedID.Size];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new EmailDB.Format.V3.EmailHashedID(raw);
    }

    private static ListingRecord SampleRecord(byte seed, long dateTicks) => new()
    {
        EmailHashedId = IdOf(seed),
        ContentBlockId = new UlidGenerator().Next(),
        DateTicks = dateTicks,
        Flags = ListingFlags.Read,
        MessageSize = 4_096,
        From = $"Sender {seed} <s{seed}@example.com>",
        Subject = $"Operable-open probe subject {seed}",
        Preview = $"Preview body {seed} exercised end-to-end through the opened instance.",
    };

    /// <summary>
    /// The decisive "one ready instance" check: not merely that every composed component is
    /// non-null, but that each is genuinely <b>operable</b> against the just-opened file — the
    /// Checkpoint's resolved roots name real blocks, the folder stores round-trip real
    /// pages/directories/deltas, the Resolver resolves a block appended after open, and the
    /// LocationIndex (through its NodeStore) accepts a Put and serves the Lookup back. Runs for
    /// both a plaintext and an encrypted file so the encryption seam is exercised on the same path.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_yields_an_operable_instance_where_every_composed_component_does_real_work(bool encrypted)
    {
        const string password = "operable-open probe password";
        (encrypted
            ? EmailManager.Create(_path, new EmailManagerCreateOptions { Password = password, KdfParameters = FastParams })
            : EmailManager.Create(_path)).Value.Dispose();

        var opened = encrypted
            ? EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password })
            : EmailManager.Open(_path);
        Ok(opened);
        using var m = opened.Value;

        AssertComponentsWired(m);
        Assert.Equal(encrypted, m.IsEncrypted);

        // 1. Checkpoint values match the file: each resolved root names a real block of the right
        //    type at its recorded offset, with the BlockId the Checkpoint claims. This proves the
        //    composed Checkpoint is the file's actual commit-point snapshot, not just a non-null object.
        var metaRoot = m.Checkpoint!.MetadataRoot!;
        var metaBlock = m.BlockManager.Read(metaRoot.Offset);
        Ok(metaBlock);
        Assert.Equal(BlockType.Metadata, metaBlock.Value.Header.Type);
        Assert.Equal(metaRoot.BlockId, metaBlock.Value.Header.BlockId);

        var folderTreeRoot = m.Checkpoint.FolderTreeRoot!;
        var folderTreeBlock = m.BlockManager.Read(folderTreeRoot.Offset);
        Ok(folderTreeBlock);
        Assert.Equal(BlockType.FolderTree, folderTreeBlock.Value.Header.Type);
        Assert.Equal(folderTreeRoot.BlockId, folderTreeBlock.Value.Header.BlockId);

        if (encrypted)
        {
            // The encrypted file's Checkpoint names a KeyStore root that resolves to a real block.
            var keyStoreRoot = m.Checkpoint.KeyStoreRoot!;
            var keyStoreBlock = m.BlockManager.Read(keyStoreRoot.Offset);
            Ok(keyStoreBlock);
            Assert.Equal(BlockType.KeyStore, keyStoreBlock.Value.Header.Type);
        }

        // 2. Folders store: write a real FolderPage through the opened block manager and read it
        //    back intact. On an encrypted file the persisted block must actually be ciphertext.
        var record = SampleRecord(seed: 5, dateTicks: 638_000_000_000_000_000L);
        var page = FolderPage.FromRecords(new[] { record });
        var writtenPage = m.Folders!.WritePage(page);
        Ok(writtenPage);
        var readPage = m.Folders.ReadPage(writtenPage.Value.Offset);
        Ok(readPage);
        Assert.Equal(record.Subject, readPage.Value.Records.Single().Subject);

        var pageBlock = m.BlockManager.Read(writtenPage.Value.Offset);
        Ok(pageBlock);
        Assert.Equal(BlockType.FolderPage, pageBlock.Value.Header.Type);
        Assert.Equal(encrypted, pageBlock.Value.Header.IsEncrypted);

        // 3. FolderDirectory store: write a directory pointing at that page and read it back.
        var folderId = new UlidGenerator().Next();
        var directory = FolderPageDirectory.Create(
            folderId,
            new[] { new PageEntry(writtenPage.Value.BlockId, record.DateTicks, record.DateTicks, page.Count) });
        var writtenDir = m.FolderDirectory!.WriteDirectory(directory);
        Ok(writtenDir);
        var readDir = m.FolderDirectory.ReadDirectory(writtenDir.Value.Offset);
        Ok(readDir);
        Assert.Equal(directory.FolderId, readDir.Value.FolderId);
        Assert.Equal(1, readDir.Value.PageCount);

        // 4. FolderDeltas store: append a chained delta block and read it back.
        var appended = m.FolderDeltas!.AppendChained(directory, new[] { FolderDeltaEntry.Add(record) });
        Ok(appended);
        var readDelta = m.FolderDeltas.ReadDeltaBlock(appended.Value.DeltaLocation.Offset);
        Ok(readDelta);
        Assert.Equal(1, readDelta.Value.Count);

        // 5. Resolver resolves a block appended after open (via the runtime map at the head of the
        //    precedence chain) to its exact on-disk location.
        Assert.True(m.Resolver!.TryGetLocation(writtenPage.Value.BlockId, out var resolved));
        Assert.Equal(writtenPage.Value.Offset, resolved!.Offset);

        // 6. LocationIndex + NodeStore: a Put actually mutates the durable index (its COW B+-tree
        //    persists nodes through the composed NodeStore), and the Lookup serves the entry back.
        var before = m.LocationIndex!.Count;
        var put = m.LocationIndex.Put(
            writtenPage.Value.BlockId, writtenPage.Value.Offset, writtenPage.Value.TotalBlockLength);
        Assert.True(put.IsSuccess, put.IsFailure ? put.Error : null);
        Assert.Equal(before + 1, m.LocationIndex.Count);
        var lookup = m.LocationIndex.Lookup(writtenPage.Value.BlockId);
        Ok<BlockLocationIndex.LocationLookup>(lookup);
        Assert.True(lookup.Value.Found);
        Assert.Equal(writtenPage.Value.Offset, lookup.Value.Offset);
    }

    // -------------------------------------------- Clean fast path (no recovery) proof

    /// <summary>
    /// The story US-EMDB-84 acceptance criterion at the EmailManager level: <b>Create produces a
    /// file that reopens cleanly</b> — an <see cref="EmailManager.Open"/> immediately after
    /// <see cref="EmailManager.Create"/> takes the CLEAN fast path with NO recovery
    /// (<c>DirtyOpener</c>) invoked, for both a plaintext and an encrypted file.
    ///
    /// <para>The observable proof that recovery did not run: <see cref="EmailManager.Create"/>
    /// writes both superblock slots (A sequence 1, B sequence 2), so the file it leaves has a
    /// winning superblock at sequence 2. The clean fast path only <i>reads</i> the superblock,
    /// whereas a recovery open <i>heals</i> it — writing a fresh slot that advances the sequence
    /// (exactly what <see cref="Open_of_a_dirty_file_recovers_via_wal_replay_to_the_last_commit"/>
    /// exercises). So an opened instance whose superblock is still at the sequence Create wrote (2),
    /// with the first Checkpoint (sequence 0) adopted unchanged and <c>CleanShutdown = 1</c>, proves
    /// the open resolved to <see cref="OpenOutcomeKind.CleanOpen"/> and never entered the dirty
    /// scan / WAL-replay path.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_after_create_reopens_cleanly_via_the_fast_path_no_recovery(bool encrypted)
    {
        const string password = "clean-fast-path probe password";

        // Create leaves the file clean and reopenable; capture the superblock sequence it wrote.
        ulong createdSuperblockSequence;
        using (var created = (encrypted
            ? EmailManager.Create(_path, new EmailManagerCreateOptions { Password = password, KdfParameters = FastParams })
            : EmailManager.Create(_path)).Value)
        {
            createdSuperblockSequence = created.Superblock.SuperblockSequence;
        }
        // Slot B (sequence 2) is the winner Create leaves behind (A = 1, B = 2).
        Assert.Equal(2UL, createdSuperblockSequence);

        var opened = encrypted
            ? EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password })
            : EmailManager.Open(_path);
        Ok(opened);
        using var manager = opened.Value;

        AssertComponentsWired(manager);
        Assert.Equal(encrypted, manager.IsEncrypted);

        // Clean fast path only READS the superblock — it does not heal/rewrite it. Had recovery run,
        // it would have written a healing superblock, advancing the sequence past 2. It is unchanged,
        // so no DirtyOpener recovery was invoked: this is the OpenOutcomeKind.CleanOpen path.
        Assert.Equal(createdSuperblockSequence, manager.Superblock.SuperblockSequence);
        Assert.Equal(2UL, manager.Superblock.SuperblockSequence);
        Assert.Equal(1, manager.Superblock.CleanShutdown);

        // The open adopted the first Checkpoint (sequence 0) unchanged — recovery would have
        // adopted the newest / a fresh Checkpoint instead.
        Assert.Equal(0UL, manager.LastCheckpointSequence);
        Assert.Equal(0UL, manager.Checkpoint!.Checkpoint.CheckpointSequence);
    }

    // ----------------------------------------------------------- Dirty / recovery

    [Fact]
    public void Open_of_a_dirty_file_recovers_via_wal_replay_to_the_last_commit()
    {
        // Create a clean file, then simulate a crash that left CleanShutdown = 0 (a kill -9
        // between operations): write a fresh superblock slot carrying the dirty flag. There is
        // no uncommitted WAL, so recovery must heal the file back to its last committed
        // Checkpoint and hand back a ready read-side state.
        EmailManager.Create(_path).Value.Dispose();

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            Assert.Equal(1, loaded.Value.CleanShutdown);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            Ok(sbManager.Write(dirty));
        }

        // Open must take the recovery path and produce a ready, healed instance.
        var opened = EmailManager.Open(_path);
        Ok(opened);
        using (var manager = opened.Value)
        {
            AssertComponentsWired(manager);
            // Recovery landed on the last committed Checkpoint (sequence 0).
            Assert.Equal(0UL, manager.LastCheckpointSequence);
            Assert.Equal(0, (int)manager.LocationIndex!.Count);
        }

        // The superblock was healed to CleanShutdown = 1, so the next open is the clean fast path.
        using var verifyStream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var clean = CleanOpener.Open(verifyStream);
        Ok(clean);
        Assert.Equal(OpenOutcomeKind.CleanOpen, clean.Value.Kind);
    }
}
