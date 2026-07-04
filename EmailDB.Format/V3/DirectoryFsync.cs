using System.Runtime.InteropServices;

namespace EmailDB.Format.V3;

/// <summary>
/// Directory fsync helpers for the v3 durability discipline
/// (EmailDB_FileFormat_Spec.md Section 10.3): file creation, compaction swap
/// (atomic rename), and deletion MUST fsync the <b>containing directory</b> to
/// persist the directory entry itself. fsyncing the file makes its <i>contents</i>
/// durable; on POSIX the name → inode link lives in the directory, and a crash
/// before the directory is synced can lose the entry (a freshly created file
/// vanishes, a rename un-happens, a deleted file resurrects).
///
/// POSIX: opens the directory read-only, calls <c>fsync(2)</c> on the directory
/// file descriptor, and closes it. On macOS, <c>fcntl(F_FULLFSYNC)</c> is
/// attempted first (plain <c>fsync</c> there does not force the drive cache;
/// this mirrors what the .NET runtime does for
/// <c>FileStream.Flush(flushToDisk: true)</c>), falling back to <c>fsync</c>
/// on filesystems that do not support it.
///
/// Windows: deliberate no-op returning success. NTFS journals directory
/// metadata operations (create/rename/delete) in its own metadata log, and
/// Win32 offers no supported directory-handle fsync for persisting a directory
/// entry — opening a directory requires <c>FILE_FLAG_BACKUP_SEMANTICS</c> and
/// <c>FlushFileBuffers</c> on such a handle is not a documented durability
/// mechanism for the entry. Durable rename on Windows is instead provided by
/// <c>ReplaceFile</c> / <c>MOVEFILE_WRITE_THROUGH</c> semantics at the rename
/// call site (spec Section 11.2).
///
/// fsync failure is surfaced, never retried (spec Section 10.3): a failed
/// directory fsync returns a failed <see cref="Result"/> and the caller must
/// treat the directory entry's durability as unknowable — the same
/// "fsyncgate" reasoning <see cref="DurableStream"/> applies to file data.
/// </summary>
public static class DirectoryFsync
{
    /// <summary>
    /// fsyncs the directory at <paramref name="directoryPath"/> so directory
    /// entries created, renamed, or deleted inside it are durable. Call after
    /// file create, rename, and delete (spec Sections 10.3, 11.1, 11.2).
    /// No-op returning success on Windows (see class remarks).
    /// </summary>
    /// <param name="directoryPath">Path of the directory to fsync.</param>
    public static Result Sync(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (OperatingSystem.IsWindows())
        {
            // Deliberate no-op: NTFS journals directory metadata itself and
            // Win32 has no supported directory fsync (see class remarks).
            return Result.Success();
        }

        if (!Directory.Exists(directoryPath))
            return Result.Failure(
                $"Cannot fsync directory '{directoryPath}': it does not exist or is not a directory.");

        return SyncPosix(directoryPath);
    }

    /// <summary>
    /// fsyncs the directory containing <paramref name="path"/> (a file that was
    /// just created, renamed to, or deleted). Relative paths are resolved
    /// against the current directory before taking the parent.
    /// </summary>
    /// <param name="path">Path of the file whose containing directory to fsync.</param>
    public static Result SyncContainingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null)
            return Result.Failure(
                $"Cannot fsync the containing directory of '{path}': it has no containing directory (filesystem root).");

        return Sync(directory);
    }

    private static Result SyncPosix(string directoryPath)
    {
        // Open the directory itself; O_RDONLY (0) is all fsync(2) on a
        // directory fd requires. O_DIRECTORY is deliberately not used: its
        // value is architecture-dependent on Linux, and Directory.Exists above
        // already guarantees the path names a directory.
        int fd;
        do
        {
            fd = open(directoryPath, O_RDONLY);
        }
        while (fd < 0 && Marshal.GetLastPInvokeError() == EINTR);

        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            return Result.Failure(
                $"Cannot fsync directory '{directoryPath}': open failed with errno {errno} " +
                $"({Marshal.GetPInvokeErrorMessage(errno)}).");
        }

        try
        {
            // macOS: fsync(2) does not force the drive's cache; F_FULLFSYNC
            // does. Fall back to fsync on filesystems that reject it — the
            // same strategy the .NET runtime uses for Flush(flushToDisk: true).
            if (OperatingSystem.IsMacOS() && fcntl(fd, F_FULLFSYNC, 0) >= 0)
                return Result.Success();

            // fsync failure is never retried (spec Section 10.3).
            if (fsync(fd) != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                return Result.Failure(
                    $"fsync of directory '{directoryPath}' failed with errno {errno} " +
                    $"({Marshal.GetPInvokeErrorMessage(errno)}). Durability of directory " +
                    "entries created, renamed, or deleted in it is unknowable; fsync is " +
                    "never retried (spec Section 10.3).");
            }

            return Result.Success();
        }
        finally
        {
            // Close errors on a read-only directory fd after the sync verdict
            // is already decided carry no durability information; ignore them.
            _ = close(fd);
        }
    }

    // ---- libc interop (POSIX only; never reached on Windows) ----

    private const int O_RDONLY = 0;   // Linux and macOS
    private const int EINTR = 4;      // Linux and macOS
    private const int F_FULLFSYNC = 51; // macOS fcntl(2) command

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
