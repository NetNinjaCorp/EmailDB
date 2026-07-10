using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="EmailManager.Close"/> (US-EMDB-84-7,
/// EmailDB_FileFormat_Spec.md Section 11.1): the durable, clean counterpart to
/// <see cref="EmailManager.Open"/>. Close flushes this session's appends into the
/// location index, writes a final Checkpoint reflecting the current roots, writes a
/// CleanShutdown = 1 superblock repointing its LastCheckpoint hint, and releases the
/// writer lock.
///
/// <para>The decisive cases are: after Close the file reopens on the <b>clean</b> fast
/// path (no recovery) with state intact; blocks appended before Close are durable and
/// resolvable after reopen; the writer lock is released so a second Open succeeds;
/// double-Close and Dispose-after-Close are safe; and a session left <i>dirty</i> (no
/// Close) recovers via WAL replay on the next Open.</para>
/// </summary>
public class EmailManagerCloseV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-close-{Guid.NewGuid():N}.emdb");

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) keep the KDF fast in tests.
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRawStream() =>
        new(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

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
        Subject = $"Close probe subject {seed}",
        Preview = $"Preview body {seed} written before close and read back after reopen.",
    };

    // ------------------------------------------------------ Clean close / reopen

    [Fact]
    public void Close_writes_a_final_checkpoint_and_the_file_reopens_on_the_clean_path()
    {
        EmailManager.Create(_path).Value.Dispose();

        ulong finalSequence;
        using (var opened = EmailManager.Open(_path).Value)
        {
            Assert.Equal(0UL, opened.LastCheckpointSequence);
            Ok(opened.Close());
            finalSequence = opened.LastCheckpointSequence; // still the loaded value on the instance
        }

        // Reopen via the clean-open fast path (no recovery): the acceptance criterion.
        using (var stream = OpenRawStream())
        {
            var clean = CleanOpener.Open(stream);
            Ok(clean);
            Assert.Equal(OpenOutcomeKind.CleanOpen, clean.Value.Kind);
            using var state = clean.Value.State!;
            // Close advanced the Checkpoint: the final one is sequence 1 (create wrote sequence 0).
            Assert.Equal(1UL, state.Checkpoint.Checkpoint.CheckpointSequence);
        }

        // A full Open of the closed file lands cleanly with all components wired.
        using var reopened = EmailManager.Open(_path).Value;
        Assert.True(reopened.IsOpen);
        Assert.Equal(1, reopened.Superblock.CleanShutdown);
        Assert.Equal(1UL, reopened.LastCheckpointSequence);
        Assert.Equal(0, (int)reopened.LocationIndex!.Count);
        _ = finalSequence;
    }

    [Fact]
    public void Close_makes_this_sessions_appends_durable_and_resolvable_after_reopen()
    {
        EmailManager.Create(_path).Value.Dispose();

        // Open, append three folder pages (each registers in the runtime map), then Close — which
        // must fold them into the durable location index and name it in the final Checkpoint.
        var pageIds = new List<byte[]>();
        var offsets = new List<long>();
        using (var opened = EmailManager.Open(_path).Value)
        {
            for (byte i = 0; i < 3; i++)
            {
                var record = SampleRecord(seed: (byte)(10 + i), dateTicks: 638_000_000_000_000_000L + i);
                var written = opened.Folders!.WritePage(FolderPage.FromRecords(new[] { record }));
                Ok(written);
                pageIds.Add((byte[])written.Value.BlockId.Clone());
                offsets.Add(written.Value.Offset);
            }
            Ok(opened.Close());
        }

        // Reopen: the three appended blocks are now in the durable location index and resolve.
        using var reopened = EmailManager.Open(_path).Value;
        Assert.Equal(3, (int)reopened.LocationIndex!.Count);
        for (int i = 0; i < pageIds.Count; i++)
        {
            Assert.True(reopened.Resolver!.TryGetLocation(pageIds[i], out var loc),
                $"page {i} appended before Close must resolve after reopen.");
            Assert.Equal(offsets[i], loc!.Offset);

            // The page content is durable and reads back through the reopened folder store.
            var read = reopened.Folders!.ReadPage(loc.Offset);
            Ok(read);
            Assert.Equal($"Close probe subject {(byte)(10 + i)}", read.Value.Records.Single().Subject);
        }
    }

    [Fact]
    public void Close_encrypted_reopens_cleanly_with_the_password()
    {
        const string password = "close probe password";
        EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = password,
            KdfParameters = FastParams,
        }).Value.Dispose();

        using (var opened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password }).Value)
        {
            var record = SampleRecord(seed: 40, dateTicks: 638_000_000_000_000_000L);
            Ok(opened.Folders!.WritePage(FolderPage.FromRecords(new[] { record })));
            Ok(opened.Close());
        }

        using var reopened = EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password }).Value;
        Assert.True(reopened.IsEncrypted);
        Assert.Equal(1, reopened.Superblock.CleanShutdown);
        Assert.Equal(1UL, reopened.LastCheckpointSequence);
        Assert.Equal(1, (int)reopened.LocationIndex!.Count);
        Assert.NotNull(reopened.Checkpoint!.KeyStoreRoot);
    }

    [Fact]
    public void Close_sets_CleanShutdown_and_repoints_the_hint_on_disk_read_at_the_file_level()
    {
        EmailManager.Create(_path).Value.Dispose();

        long createCheckpointOffset;
        using (var opened = EmailManager.Open(_path).Value)
        {
            // The hint on disk at open names the create Checkpoint (sequence 0).
            createCheckpointOffset = opened.LastCheckpoint.Offset;

            // Mutate this session so the final Checkpoint has something new to reflect.
            var record = SampleRecord(seed: 70, dateTicks: 638_000_000_000_000_000L);
            Ok(opened.Folders!.WritePage(FolderPage.FromRecords(new[] { record })));
            Ok(opened.Close());
        }

        // Read the superblock DIRECTLY off disk (no reopen/recovery, which would itself heal a dirty
        // file to CleanShutdown = 1): Close itself must have persisted CleanShutdown = 1 and repointed
        // the LastCheckpoint hint at the newer final Checkpoint (appended after create's, so a greater
        // offset). This isolates leg 2 (the flag) and leg 1 (the hint advanced) from the open path.
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        var loaded = sbManager.Load();
        Ok(loaded);
        Assert.Equal(1, loaded.Value.CleanShutdown);
        Assert.True(loaded.Value.LastCheckpointOffset > createCheckpointOffset,
            "Close must repoint the on-disk LastCheckpoint hint at the newer final Checkpoint.");
    }

    // ---------------------------------------------------------- Writer lock release

    [Fact]
    public void Close_releases_the_writer_lock_so_a_later_open_succeeds()
    {
        EmailManager.Create(_path).Value.Dispose();

        var first = EmailManager.Open(_path).Value;

        // While the file is still open the writer lock is held: a concurrent Open must fail rather
        // than hand out a second writer (spec Section 12, single-writer lock).
        var blocked = EmailManager.Open(_path);
        Assert.True(blocked.IsFailure, "a second Open must fail while the first still holds the writer lock.");

        Ok(first.Close());

        // The writer lock is free after Close: a fresh Open takes it.
        var second = EmailManager.Open(_path);
        Ok(second);
        second.Value.Dispose();
    }

    // ------------------------------------------------------------ Idempotence

    [Fact]
    public void Double_close_is_a_success_no_op()
    {
        EmailManager.Create(_path).Value.Dispose();

        var opened = EmailManager.Open(_path).Value;
        Ok(opened.Close());
        Ok(opened.Close()); // second Close is a no-op, not a fault.
        opened.Dispose();   // Dispose after Close is safe.
    }

    [Fact]
    public void Dispose_after_close_is_safe_and_reopen_stays_clean()
    {
        EmailManager.Create(_path).Value.Dispose();

        var opened = EmailManager.Open(_path).Value;
        Ok(opened.Close());
        opened.Dispose();
        opened.Dispose(); // double Dispose is safe too.

        using var reopened = EmailManager.Open(_path).Value;
        Assert.Equal(1, reopened.Superblock.CleanShutdown);
    }

    [Fact]
    public void Close_on_a_created_but_never_opened_instance_just_releases_cleanly()
    {
        var created = EmailManager.Create(_path).Value;
        Assert.False(created.IsOpen);
        Ok(created.Close()); // no composed read-side: releases the writer lock, file already clean.

        // The file created+cleanly-released reopens on the clean fast path.
        using var reopened = EmailManager.Open(_path).Value;
        Assert.Equal(1, reopened.Superblock.CleanShutdown);
        Assert.Equal(0UL, reopened.LastCheckpointSequence);
    }

    // -------------------------------------------------------- Dirty (no Close) recovery

    [Fact]
    public void Dispose_without_close_leaves_a_dirty_file_that_recovers_on_next_open()
    {
        EmailManager.Create(_path).Value.Dispose();

        // Open the file and Dispose WITHOUT Close — a dirty close. To make the session's dirtiness
        // observable with the current composition (Open does not itself mark the superblock dirty),
        // simulate the crash the dirty-close contract protects against: leave CleanShutdown = 0 on
        // disk, exactly as a kill -9 between operations would.
        using (var opened = EmailManager.Open(_path).Value)
        {
            // No Close: the instance is disposed by the using-block (dirty close, superblock untouched).
        }
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0;
            Ok(sbManager.Write(dirty));
        }

        // The next Open must take the recovery path and heal the file back to a clean read-side.
        using (var recovered = EmailManager.Open(_path).Value)
        {
            Assert.True(recovered.IsOpen);
            Assert.Equal(1, recovered.Superblock.CleanShutdown); // healed by recovery.
            Ok(recovered.Close());
        }

        // After recovery + Close the file is clean again on the fast path.
        using var verify = OpenRawStream();
        var clean = CleanOpener.Open(verify);
        Ok(clean);
        Assert.Equal(OpenOutcomeKind.CleanOpen, clean.Value.Kind);
    }
}
