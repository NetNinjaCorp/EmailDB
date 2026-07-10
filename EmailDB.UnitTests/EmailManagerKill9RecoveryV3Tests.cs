using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies the story US-EMDB-84 acceptance criterion "<b>Kill -9 between operations
/// always reopens via recovery to the last commit</b>" (US-EMDB-84-4) at the
/// <see cref="EmailManager"/> level.
///
/// <para>A "kill -9 between operations" is modelled the way the rest of this suite models
/// a hard process death (see <c>EmailManagerCloseV3Tests</c>): an in-flight session
/// appends real blocks and is then abandoned <b>without</b> <see cref="EmailManager.Close"/>
/// — no final Checkpoint, the writer just goes away — and the on-disk superblock is left
/// with <c>CleanShutdown = 0</c>, exactly the state a crash leaves. Because the current
/// EmailManager write path produces no WAL, the appended tail sits past the last committed
/// Checkpoint with nothing to replay it, so "recover to the last commit" means: adopt the
/// last durable Checkpoint, keep every committed operation, and drop the uncommitted tail.</para>
///
/// <para>The decisive dimension the single-cut-point dirty tests elsewhere do not cover is
/// <b>"always" / "between operations"</b>: the crash is exercised at several cut points —
/// right after <see cref="EmailManager.Create"/>, and after 1 and 3 <i>committed</i> sessions
/// (each <see cref="EmailManager.Close"/> advances the Checkpoint) — and in every case the
/// reopen must take the recovery path (not the clean fast path), land on the last committed
/// Checkpoint, expose every committed block, and expose none of the uncommitted tail.</para>
/// </summary>
public class EmailManagerKill9RecoveryV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-kill9-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static EmailDB.Format.V3.EmailHashedID IdOf(byte seed)
    {
        var raw = new byte[EmailDB.Format.V3.EmailHashedID.Size];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(seed + i);
        return new EmailDB.Format.V3.EmailHashedID(raw);
    }

    private static ListingRecord SampleRecord(byte seed) => new()
    {
        EmailHashedId = IdOf(seed),
        ContentBlockId = new UlidGenerator().Next(),
        DateTicks = 638_000_000_000_000_000L + seed,
        Flags = ListingFlags.Read,
        MessageSize = 4_096,
        From = $"Sender {seed} <s{seed}@example.com>",
        Subject = $"Kill-9 probe subject {seed}",
        Preview = $"Preview body {seed} written to exercise crash recovery to the last commit.",
    };

    /// <summary>
    /// A durable, committed operation: open, append <paramref name="pages"/> folder pages,
    /// and <see cref="EmailManager.Close"/> — which folds them into the location index and
    /// advances the Checkpoint by one. Returns the appended page BlockIds.
    /// </summary>
    private List<byte[]> CommitSession(int pages, ref byte seed)
    {
        var ids = new List<byte[]>();
        using var opened = EmailManager.Open(_path).Value;
        for (var i = 0; i < pages; i++)
        {
            var written = opened.Folders!.WritePage(FolderPage.FromRecords(new[] { SampleRecord(seed++) }));
            Ok(written);
            ids.Add((byte[])written.Value.BlockId.Clone());
        }
        Ok(opened.Close());
        return ids;
    }

    /// <summary>
    /// A kill -9: open, append <paramref name="pages"/> folder pages (the uncommitted tail),
    /// abandon the manager WITHOUT Close (dispose only — no final Checkpoint), then stamp the
    /// on-disk superblock <c>CleanShutdown = 0</c> as a hard crash would leave it. Returns the
    /// appended (uncommitted) page BlockIds — which recovery must NOT surface.
    /// </summary>
    private List<byte[]> Kill9AfterAppending(int pages, ref byte seed)
    {
        var ids = new List<byte[]>();
        var opened = EmailManager.Open(_path).Value;
        for (var i = 0; i < pages; i++)
        {
            var written = opened.Folders!.WritePage(FolderPage.FromRecords(new[] { SampleRecord(seed++) }));
            Ok(written);
            ids.Add((byte[])written.Value.BlockId.Clone());
        }
        opened.Dispose(); // process death: no Close, no final Checkpoint, writer just vanishes.

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            var loaded = sbManager.Load();
            Ok(loaded);
            var dirty = loaded.Value.Clone();
            dirty.CleanShutdown = 0; // the crash flag.
            Ok(sbManager.Write(dirty));
        }
        return ids;
    }

    [Theory]
    [InlineData(0)] // kill -9 right after Create, before any committed operation.
    [InlineData(1)] // kill -9 after one committed session.
    [InlineData(3)] // kill -9 after three committed sessions (Checkpoint advanced 3x).
    public void Kill9_between_operations_always_reopens_via_recovery_to_the_last_commit(int committedSessions)
    {
        byte seed = 1;
        var committedIds = new List<byte[]>();
        var expectedCommitted = 0;
        ulong expectedSequence = 0; // Create wrote Checkpoint sequence 0.

        EmailManager.Create(_path).Value.Dispose();

        for (var s = 0; s < committedSessions; s++)
        {
            var pages = 2 + s;
            committedIds.AddRange(CommitSession(pages, ref seed));
            expectedCommitted += pages;
            expectedSequence++; // each Close writes a fresh Checkpoint one past the last.
        }

        // Kill -9: an in-flight session appends four pages, then the process dies. The tail
        // sits past the last committed Checkpoint with no WAL to replay it.
        var uncommittedIds = Kill9AfterAppending(4, ref seed);

        // The reopen genuinely needs recovery: the clean fast path refuses the dirty file.
        using (var probeStream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var probe = CleanOpener.Open(probeStream);
            Ok(probe);
            Assert.Equal(OpenOutcomeKind.DirtyOpenRequired, probe.Value.Kind);
        }

        // Open takes the recovery path and heals the file back to the last commit.
        using (var recovered = EmailManager.Open(_path).Value)
        {
            Assert.True(recovered.IsOpen);
            // Healed by recovery (we left it 0) — proves the recovery path ran, not the clean one.
            Assert.Equal(1, recovered.Superblock.CleanShutdown);
            // Recovery landed on the last committed Checkpoint, not the abandoned tail.
            Assert.Equal(expectedSequence, recovered.LastCheckpointSequence);
            // Every committed operation is present...
            Assert.Equal(expectedCommitted, (int)recovered.LocationIndex!.Count);
            foreach (var id in committedIds)
                Assert.True(recovered.Resolver!.TryGetLocation(id, out _),
                    "a block committed before the crash must resolve after recovery.");
            // ...and no uncommitted-tail block leaked into the recovered commit (no partial state).
            foreach (var id in uncommittedIds)
                Assert.False(recovered.Resolver!.TryGetLocation(id, out _),
                    "a block appended after the last commit must be absent after recovery to the last commit.");

            Ok(recovered.Close());
        }

        // The heal is durable: the same file now opens on the clean fast path.
        using (var verifyStream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var clean = CleanOpener.Open(verifyStream);
            Ok(clean);
            Assert.Equal(OpenOutcomeKind.CleanOpen, clean.Value.Kind);
            clean.Value.State!.Dispose();
        }

        // A full reopen still sees exactly the committed state — nothing lost, nothing partial.
        using (var reopened = EmailManager.Open(_path).Value)
            Assert.Equal(expectedCommitted, (int)reopened.LocationIndex!.Count);
    }
}
