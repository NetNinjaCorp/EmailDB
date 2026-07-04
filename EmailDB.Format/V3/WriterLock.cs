namespace EmailDB.Format.V3;

/// <summary>
/// Typed refusal error thrown when <see cref="WriterLock.Acquire"/> finds another
/// writer already holding the single-writer lock (EmailDB_FileFormat_Spec.md
/// Section 12): a second writer MUST fail fast with a clear error, not corrupt.
/// This is not corruption and not a generic I/O failure — the file is healthy,
/// it is simply owned by another live writer. Callers catch this type to
/// distinguish "database is busy" from real I/O errors.
/// </summary>
public sealed class WriterLockException : IOException
{
    /// <summary>Path of the EmailDB file whose writer lock is held elsewhere.</summary>
    public string DatabasePath { get; }

    /// <summary>Path of the sidecar lock file the exclusive OS lock lives on.</summary>
    public string LockFilePath { get; }

    public WriterLockException(string databasePath, string lockFilePath, IOException inner)
        : base($"Another writer holds the single-writer lock for '{databasePath}' " +
               $"(exclusive OS lock on '{lockFilePath}'). A second writer must fail fast " +
               "(spec Section 12); retry after the other writer closes, or open read-only " +
               $"via {nameof(WriterLock)}.{nameof(WriterLock.OpenReader)}.", inner)
    {
        DatabasePath = databasePath;
        LockFilePath = lockFilePath;
    }
}

/// <summary>
/// Single-writer OS lock for an EmailDB file (EmailDB_FileFormat_Spec.md
/// Section 12): the writer holds an OS-level exclusive lock for the file's
/// lifetime, readers open read-shared, and a second writer fails fast with the
/// typed <see cref="WriterLockException"/>.
///
/// The exclusive lock lives on a sidecar lock file (<c>&lt;name&gt;.emdb.lock</c>)
/// opened with <see cref="FileShare.None"/>. On Windows that is a mandatory
/// sharing-mode exclusion; on POSIX, .NET maps <see cref="FileShare.None"/> to a
/// non-blocking <c>flock(LOCK_EX)</c> on the lock file's open file description.
/// The lock is released by closing the handle (<see cref="Dispose"/>, or process
/// death), never by the lock file's presence — a stale lock file left on disk is
/// harmless and is deliberately never deleted (unlink + recreate would let two
/// writers hold exclusive locks on different inodes of the same path).
///
/// Why a sidecar rather than a lock on the database file itself — verified
/// empirically on Linux (see WriterLockTests):
/// <list type="bullet">
/// <item><see cref="FileShare.Read"/> on the writer handle enforces single-writer
/// on Windows only. .NET on Linux maps it to a *shared* flock, so two writers
/// opening with <see cref="FileShare.Read"/> both succeed — FileShare alone does
/// not suffice per spec.</item>
/// <item><see cref="FileShare.None"/> on the database file itself takes
/// <c>flock(LOCK_EX)</c>, but .NET readers implicitly take a shared flock on
/// open, so readers would be locked out — the spec requires readers to open
/// shared while the writer is live.</item>
/// <item><see cref="FileStream.Lock"/> uses POSIX process-associated record
/// locks on this runtime: no conflict between two handles in one process, and
/// closing ANY same-process descriptor of the file drops the lock (the classic
/// fcntl footgun) — an in-process reader closing would silently release the
/// writer lock.</item>
/// </list>
/// flock on a sidecar has none of these defects: it excludes second writers both
/// in-process and cross-process, survives unrelated descriptor closes, and never
/// touches the file readers open. Like all POSIX advisory locking it binds
/// cooperating processes; the Windows sharing mode on the writer handle
/// (<see cref="FileShare.Read"/>, per spec) additionally hard-blocks rogue
/// writers there.
///
/// The writer's database handle is exposed as <see cref="Stream"/> and is the
/// stream handed to <see cref="SuperblockManager"/> / <see cref="SuperblockSession"/>
/// and <see cref="BlockManager"/> for the session's lifetime. Readers use
/// <see cref="OpenReader"/>, which opens read-shared with
/// <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> so long-lived
/// readers coexist with the writer and survive a compaction swap via the old
/// inode (POSIX) / pending-delete semantics (Windows), per spec Sections 11.2 and 12.
/// </summary>
public sealed class WriterLock : IDisposable
{
    /// <summary>Suffix appended to the database path to form the sidecar lock file path.</summary>
    public const string LockFileSuffix = ".lock";

    private readonly FileStream _lockStream;
    private bool _disposed;

    private WriterLock(string databasePath, string lockFilePath, FileStream lockStream, FileStream stream)
    {
        DatabasePath = databasePath;
        LockFilePath = lockFilePath;
        _lockStream = lockStream;
        Stream = stream;
    }

    /// <summary>Path of the locked EmailDB file.</summary>
    public string DatabasePath { get; }

    /// <summary>Path of the sidecar lock file holding the exclusive OS lock.</summary>
    public string LockFilePath { get; }

    /// <summary>
    /// The writer's read-write database handle, opened with
    /// <see cref="FileShare.Read"/> (spec Section 12). Hand this stream to
    /// <see cref="SuperblockManager"/> and <see cref="BlockManager"/>; it lives
    /// exactly as long as the lock and is disposed by <see cref="Dispose"/>.
    /// </summary>
    public FileStream Stream { get; }

    /// <summary>
    /// Acquires the single-writer lock for <paramref name="databasePath"/> and
    /// opens the writer's database handle. The lock is taken (non-blocking)
    /// BEFORE the database file is opened or created, so a losing writer never
    /// touches the database file at all. Held until <see cref="Dispose"/>.
    /// When this call creates the database file and/or the sidecar lock file,
    /// the containing directory is fsynced via
    /// <see cref="DirectoryFsync.SyncContainingDirectory"/> so the new
    /// directory entries are durable (spec Sections 10.3, 11.1); a failed
    /// directory fsync releases the lock and throws <see cref="IOException"/>.
    /// </summary>
    /// <param name="databasePath">Path of the EmailDB file (e.g. <c>mail.emdb</c>).</param>
    /// <param name="mode">
    /// Open mode for the database file: <see cref="FileMode.OpenOrCreate"/>
    /// (default), <see cref="FileMode.Open"/> for an existing file, or
    /// <see cref="FileMode.CreateNew"/> when initializing (spec Section 11.1).
    /// </param>
    /// <exception cref="WriterLockException">
    /// Another writer holds the lock — the second writer fails fast (spec Section 12).
    /// </exception>
    public static WriterLock Acquire(string databasePath, FileMode mode = FileMode.OpenOrCreate)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        if (mode is FileMode.Truncate or FileMode.Append)
            throw new ArgumentException(
                $"FileMode.{mode} is not valid for an EmailDB writer: the format is append-only " +
                "with a fixed superblock region (spec Section 2).", nameof(mode));

        var lockFilePath = databasePath + LockFileSuffix;

        // Directory fsync (spec Sections 10.3, 11.1) is owed only when this
        // call CREATES a directory entry — record what already exists first.
        bool lockFileExisted = File.Exists(lockFilePath);
        bool databaseExisted = File.Exists(databasePath);

        FileStream lockStream;
        try
        {
            // FileShare.None → Windows sharing-mode exclusion / POSIX flock(LOCK_EX | LOCK_NB).
            // Fails fast: neither implementation blocks waiting for the holder.
            lockStream = new FileStream(
                lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsLockConflict(ex))
        {
            throw new WriterLockException(databasePath, lockFilePath, ex);
        }

        FileStream stream;
        try
        {
            // The spec Section 12 writer handle: read-write, FileShare.Read so
            // readers open concurrently (and rogue writers are hard-blocked on Windows).
            stream = new FileStream(databasePath, mode, FileAccess.ReadWrite, FileShare.Read);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }

        // File creation MUST be followed by an fsync of the containing
        // directory so the new directory entry itself is durable (spec
        // Sections 10.3, 11.1) — fsyncing the file alone does not persist the
        // name → inode link. The sidecar lives in the same directory as the
        // database (lock path = database path + suffix), so one sync covers
        // both. Failure is surfaced, never retried (spec Section 10.3): the
        // handles are released and the caller gets the error.
        if (!databaseExisted || !lockFileExisted)
        {
            var sync = DirectoryFsync.SyncContainingDirectory(databasePath);
            if (sync.IsFailure)
            {
                stream.Dispose();
                lockStream.Dispose();
                throw new IOException(
                    $"Created '{databasePath}' but could not make its directory entry " +
                    $"durable (spec Sections 10.3, 11.1): {sync.Error}");
            }
        }

        return new WriterLock(databasePath, lockFilePath, lockStream, stream);
    }

    /// <summary>
    /// Opens a read-shared handle on the database file (spec Section 12): readers
    /// coexist with the live writer (<see cref="FileShare.ReadWrite"/>) and
    /// survive the compaction swap (<see cref="FileShare.Delete"/> — old inode on
    /// POSIX, pending-delete on Windows, spec Section 11.2). Any number of
    /// readers may hold such handles concurrently; none of them touches the
    /// writer lock.
    /// </summary>
    /// <param name="databasePath">Path of the EmailDB file.</param>
    public static FileStream OpenReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        return new FileStream(
            databasePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    /// True when the IOException reports the exclusive open lost to an existing
    /// holder, as opposed to a real I/O failure. Windows: ERROR_SHARING_VIOLATION
    /// (32) / ERROR_LOCK_VIOLATION (33). POSIX: EAGAIN/EWOULDBLOCK (11 on Linux,
    /// 35 on BSD/macOS — the errno .NET surfaces as HResult when flock(LOCK_NB)
    /// loses; verified empirically as 11 on Linux), plus EACCES (13, the
    /// POSIX-permitted alternative) and EBUSY (16).
    /// </summary>
    private static bool IsLockConflict(IOException ex)
    {
        // These IOException subtypes are path problems, never lock contention.
        if (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
            return false;

        if (OperatingSystem.IsWindows())
            return (ex.HResult & 0xFFFF) is 32 or 33;

        return ex.HResult is 11 or 13 or 16 or 35;
    }

    /// <summary>
    /// Closes the writer's database handle, then releases the exclusive OS lock
    /// by closing the lock-file handle — in that order, so no other writer can
    /// start while this one's handle is still open. The sidecar lock file itself
    /// is never deleted (deletion is racy on POSIX; a stale lock file is inert
    /// because the lock lives on the handle, not the file's existence).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stream.Dispose();
        _lockStream.Dispose();
    }
}
