using System.Diagnostics;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the single-writer OS lock (EmailDB_FileFormat_Spec.md Section 12):
/// a second writer fails fast with the typed <see cref="WriterLockException"/>,
/// readers open shared while the writer holds the lock, the lock is released on
/// dispose, and — because the lock is a sidecar flock, not a POSIX record lock —
/// an in-process reader closing its handle does not drop the writer lock.
/// Includes a genuine second-process check via flock(1) on Linux, which uses the
/// same flock(2) family .NET maps FileShare to on POSIX.
/// </summary>
public class WriterLockTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-writerlock-{Guid.NewGuid():N}.emdb");

    private string LockPath => _path + WriterLock.LockFileSuffix;

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
        if (File.Exists(LockPath))
            File.Delete(LockPath);
    }

    // ---- Acquire: writer handle and lock file ----

    [Fact]
    public void Acquire_OpensWriterHandleAndCreatesSidecarLockFile()
    {
        using var writer = WriterLock.Acquire(_path);

        Assert.Equal(_path, writer.DatabasePath);
        Assert.Equal(LockPath, writer.LockFilePath);
        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(LockPath));

        // The stream satisfies SuperblockManager/BlockManager requirements.
        Assert.True(writer.Stream.CanRead);
        Assert.True(writer.Stream.CanWrite);
        Assert.True(writer.Stream.CanSeek);
    }

    [Fact]
    public void Acquire_RejectsTruncateAndAppendModes()
    {
        Assert.Throws<ArgumentException>(() => WriterLock.Acquire(_path, FileMode.Truncate));
        Assert.Throws<ArgumentException>(() => WriterLock.Acquire(_path, FileMode.Append));
    }

    // ---- Second writer fails fast with the typed error ----

    [Fact]
    public void SecondWriter_FailsFast_WithTypedError()
    {
        using var first = WriterLock.Acquire(_path);

        var stopwatch = Stopwatch.StartNew();
        var ex = Assert.Throws<WriterLockException>(() => WriterLock.Acquire(_path));
        stopwatch.Stop();

        // Fail FAST: the lock attempt is non-blocking, never waits for the holder.
        Assert.True(stopwatch.ElapsedMilliseconds < 5000,
            $"Second writer took {stopwatch.ElapsedMilliseconds} ms; must fail fast, not block.");

        // Typed and self-describing: callers can catch WriterLockException and
        // inspect which database is busy (spec Section 12).
        Assert.Equal(_path, ex.DatabasePath);
        Assert.Equal(LockPath, ex.LockFilePath);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    [Fact]
    public void SecondWriter_Error_IsClear_NamesFileAndRemedy()
    {
        using var first = WriterLock.Acquire(_path);

        var ex = Assert.Throws<WriterLockException>(() => WriterLock.Acquire(_path));

        // "Clear error" (spec Section 12): the message must say WHAT is locked
        // (both paths), WHY it failed (another writer), and WHAT to do about it
        // (retry later or open read-only) — no generic "sharing violation".
        Assert.Contains(_path, ex.Message);
        Assert.Contains(LockPath, ex.Message);
        Assert.Contains("Another writer holds the single-writer lock", ex.Message);
        Assert.Contains("fail fast", ex.Message);
        Assert.Contains(nameof(WriterLock.OpenReader), ex.Message);
    }

    [Fact]
    public void SecondWriter_FailsFast_WhileReadersAreOpenShared()
    {
        // The acceptance criterion as one scenario: with the writer live AND
        // readers already open shared, a second writer still fails fast, and
        // the readers are completely unaffected by the failed attempt.
        using var writer = WriterLock.Acquire(_path);
        var payload = new byte[] { 0x0B, 0xAD, 0xF0, 0x0D };
        writer.Stream.Write(payload);
        writer.Stream.Flush(flushToDisk: true);

        using var readerBefore = WriterLock.OpenReader(_path);

        var stopwatch = Stopwatch.StartNew();
        var ex = Assert.Throws<WriterLockException>(() => WriterLock.Acquire(_path));
        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 5000,
            $"Second writer took {stopwatch.ElapsedMilliseconds} ms with readers open; must fail fast.");
        Assert.Equal(_path, ex.DatabasePath);

        // Pre-existing reader still works after the refused writer...
        var read = new byte[payload.Length];
        readerBefore.ReadExactly(read);
        Assert.Equal(payload, read);

        // ...and new readers still open shared — the failed writer left no lock behind.
        using var readerAfter = WriterLock.OpenReader(_path);
        readerAfter.ReadExactly(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public void SecondWriter_NeverTouchesDatabaseFile()
    {
        using var first = WriterLock.Acquire(_path);
        first.Stream.Write(new byte[] { 1, 2, 3 });
        first.Stream.Flush(flushToDisk: true);

        // Losing writer fails at the lock, before opening the database file —
        // even a destructive mode like CreateNew never reaches it.
        Assert.Throws<WriterLockException>(() => WriterLock.Acquire(_path, FileMode.Open));
        Assert.Equal(3, new FileInfo(_path).Length);
    }

    // ---- Readers open shared while the writer holds the lock ----

    [Fact]
    public void Readers_OpenShared_WhileWriterHoldsLock()
    {
        using var writer = WriterLock.Acquire(_path);
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        writer.Stream.Write(payload);
        writer.Stream.Flush(flushToDisk: true);

        // Multiple concurrent readers, all while the writer lock is held.
        using var reader1 = WriterLock.OpenReader(_path);
        using var reader2 = WriterLock.OpenReader(_path);

        var read = new byte[payload.Length];
        reader1.ReadExactly(read);
        Assert.Equal(payload, read);
        reader2.ReadExactly(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public void ReaderClose_DoesNotReleaseWriterLock()
    {
        // Guards the POSIX record-lock footgun: with fcntl process-associated
        // locks, closing ANY same-process descriptor of the locked file drops
        // the lock. The sidecar flock must survive a reader open/close cycle.
        using var writer = WriterLock.Acquire(_path);
        writer.Stream.WriteByte(0x42);
        writer.Stream.Flush();

        var reader = WriterLock.OpenReader(_path);
        reader.ReadByte();
        reader.Dispose();

        Assert.Throws<WriterLockException>(() => WriterLock.Acquire(_path));
    }

    // ---- Lock lifetime: released on dispose, held for the writer's lifetime ----

    [Fact]
    public void Dispose_ReleasesLock_NextWriterAcquires()
    {
        var first = WriterLock.Acquire(_path);
        first.Stream.WriteByte(0x01);
        first.Dispose();

        using var second = WriterLock.Acquire(_path);
        Assert.True(second.Stream.CanWrite);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var writer = WriterLock.Acquire(_path);
        writer.Dispose();
        writer.Dispose();
    }

    // ---- Directory fsync on file creation (spec Sections 10.3, 11.1) ----

    [Fact]
    public void Acquire_CreatingFiles_FsyncsDirectory_FailureSurfacesAndReleasesLock()
    {
        // Proves the file-CREATE call site really invokes the directory fsync,
        // not merely that the helper works when called by hand: a directory
        // with mode 0300 (write+execute, no read) still allows creating files
        // inside it, but open(dir, O_RDONLY) — the first step of the directory
        // fsync — fails with EACCES. If Acquire skipped the fsync, it would
        // succeed here; instead the fsync failure must surface as IOException.
        // POSIX-only (Windows fsync is a documented no-op) and meaningless as
        // root, which bypasses permission checks.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        string dir = Path.Combine(Path.GetTempPath(), $"emaildb-wl-dirsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string db = Path.Combine(dir, "mail.emdb");
            File.SetUnixFileMode(dir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var ex = Assert.Throws<IOException>(() => WriterLock.Acquire(db));
            Assert.Contains("durable", ex.Message);
            Assert.Contains(db, ex.Message);

            // The failed Acquire released both handles: with the directory
            // readable again, a fresh writer acquires (and this time the
            // directory fsync for the already-created entries succeeds).
            File.SetUnixFileMode(
                dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var writer = WriterLock.Acquire(db);
            Assert.True(writer.Stream.CanWrite);
        }
        finally
        {
            File.SetUnixFileMode(
                dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Acquire_ExistingFiles_SkipsDirectoryFsync()
    {
        // The inverse guard: opening EXISTING files creates no directory entry,
        // so no directory fsync is owed (spec Section 10.3 ties it to create,
        // rename, and delete). Same unreadable-directory trap as above — if
        // Acquire fsynced unconditionally it would fail here, so success proves
        // the fsync is correctly conditioned on creation.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            return;

        string dir = Path.Combine(Path.GetTempPath(), $"emaildb-wl-dirskip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string db = Path.Combine(dir, "mail.emdb");
            using (WriterLock.Acquire(db)) { } // creates database + lock file while readable

            File.SetUnixFileMode(dir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var writer = WriterLock.Acquire(db, FileMode.Open);
            Assert.True(writer.Stream.CanWrite);
        }
        finally
        {
            File.SetUnixFileMode(
                dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Cross-process: a genuine second process loses the flock (Linux) ----

    [Fact]
    public void CrossProcess_SecondProcessCannotTakeLock_UntilDisposed()
    {
        // .NET maps FileShare.None to flock(LOCK_EX) on POSIX, the same lock
        // family as flock(1), so a child flock -n is a true second-process
        // writer-lock attempt. Windows/macOS or missing flock(1): covered by
        // the in-process tests above (Windows sharing modes and flock conflict
        // per-handle, so in-process conflict implies cross-process conflict).
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/flock"))
            return;

        var writer = WriterLock.Acquire(_path);
        try
        {
            Assert.Equal(1, RunFlockAttempt());
        }
        finally
        {
            writer.Dispose();
        }

        Assert.Equal(0, RunFlockAttempt());
    }

    /// <summary>Runs `flock -n LockPath -c true` and returns its exit code (1 = lock held elsewhere).</summary>
    private int RunFlockAttempt()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/flock",
            ArgumentList = { "-n", LockPath, "-c", "true" },
            UseShellExecute = false,
        })!;
        Assert.True(process.WaitForExit(10_000), "flock(1) child did not exit within 10 s.");
        return process.ExitCode;
    }

    // ---- Integration: the writer handle feeds the v3 managers ----

    [Fact]
    public void WriterStream_DrivesSuperblockSessionAndBlockManager()
    {
        byte[] blockId;
        long blockOffset;

        using (var writer = WriterLock.Acquire(_path))
        {
            var created = SuperblockSession.Create(writer.Stream, new Superblock());
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error : null);
            using var session = created.Value;

            using var blocks = new BlockManager(writer.Stream, session.MaxPayloadLength);
            var appended = blocks.Append(
                BlockType.Metadata, PayloadEncoding.Json, new byte[] { 10, 20, 30 });
            Assert.True(appended.IsSuccess, appended.IsFailure ? appended.Error : null);
            blockId = appended.Value.BlockId;
            blockOffset = appended.Value.Offset;
            Assert.True(blocks.Flush().IsSuccess);

            // While the writer lock is held, a shared reader sees the flushed block.
            using var readerStream = WriterLock.OpenReader(_path);
            readerStream.Seek(blockOffset, SeekOrigin.Begin);
            var headerBytes = new byte[BlockSerializer.SerializedHeaderSize];
            readerStream.ReadExactly(headerBytes);
            var header = BlockSerializer.DeserializeHeader(headerBytes, session.MaxPayloadLength);
            Assert.True(header.IsSuccess, header.IsFailure ? header.Error : null);
            Assert.Equal(blockId, header.Value.BlockId);

            Assert.True(session.Close().IsSuccess);
        }

        // Lock released with the session over: the next writer opens and reads back.
        using (var writer = WriterLock.Acquire(_path, FileMode.Open))
        {
            using var blocks = new BlockManager(writer.Stream);
            var read = blocks.Read(blockOffset);
            Assert.True(read.IsSuccess, read.IsFailure ? read.Error : null);
            Assert.Equal(blockId, read.Value.Header.BlockId);
            Assert.Equal(new byte[] { 10, 20, 30 }, read.Value.Payload);
        }
    }
}
