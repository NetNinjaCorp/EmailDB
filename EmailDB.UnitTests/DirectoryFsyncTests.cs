using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the directory fsync helpers (EmailDB_FileFormat_Spec.md
/// Section 10.3): file create, rename, and delete must be followed by an fsync
/// of the containing directory so the directory entry itself is durable. On
/// POSIX the helper opens the directory and fsyncs its descriptor; on Windows
/// it is a documented no-op (NTFS journals directory metadata; Win32 has no
/// supported directory fsync).
/// </summary>
public class DirectoryFsyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"emaildb-dirfsync-{Guid.NewGuid():N}");

    public DirectoryFsyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---- Sync: the core directory fsync ----

    [Fact]
    public void Sync_ExistingDirectory_Succeeds()
    {
        var result = DirectoryFsync.Sync(_dir);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public void Sync_MissingDirectory_FailsWithClearError()
    {
        string missing = Path.Combine(_dir, "does-not-exist");

        var result = DirectoryFsync.Sync(missing);

        Assert.True(result.IsFailure);
        Assert.Contains(missing, result.Error);
    }

    [Fact]
    public void Sync_PathIsAFile_Fails()
    {
        // The helper syncs directories only; handing it a file is a caller bug
        // (they wanted SyncContainingDirectory) and must not silently "work"
        // by fsyncing the file instead of its directory entry.
        string file = Path.Combine(_dir, "a-file.emdb");
        File.WriteAllText(file, "x");

        var result = DirectoryFsync.Sync(file);

        Assert.True(result.IsFailure);
        Assert.Contains(file, result.Error);
    }

    [Fact]
    public void Sync_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DirectoryFsync.Sync(null!));
        Assert.Throws<ArgumentException>(() => DirectoryFsync.Sync("  "));
    }

    [Fact]
    public void Sync_IsRepeatable()
    {
        // fsync of an unchanged directory is a valid (cheap) operation; the
        // helper must not carry poison-style state of its own — poisoning
        // belongs to DurableStream. Each call stands alone.
        Assert.True(DirectoryFsync.Sync(_dir).IsSuccess);
        Assert.True(DirectoryFsync.Sync(_dir).IsSuccess);
        Assert.True(DirectoryFsync.Sync(_dir).IsSuccess);
    }

    // ---- SyncContainingDirectory: the create/rename/delete call sites ----

    [Fact]
    public void SyncContainingDirectory_AfterFileCreate_Succeeds()
    {
        string file = Path.Combine(_dir, "new.emdb");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

        var result = DirectoryFsync.SyncContainingDirectory(file);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public void SyncContainingDirectory_AfterRename_Succeeds()
    {
        // The compaction swap (spec Section 11.2): write .compact, fsync file,
        // atomic rename over the live name, fsync the directory.
        string compact = Path.Combine(_dir, "mail.emdb.compact");
        string live = Path.Combine(_dir, "mail.emdb");
        File.WriteAllText(compact, "compacted");
        File.Move(compact, live, overwrite: true);

        var result = DirectoryFsync.SyncContainingDirectory(live);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(File.Exists(live));
        Assert.False(File.Exists(compact));
    }

    [Fact]
    public void SyncContainingDirectory_AfterDelete_Succeeds()
    {
        // Deleting a leftover .compact file on open (spec Section 11.2) must
        // also persist the removal of the directory entry. The file is gone,
        // so the helper must sync the parent of the *path*, never open the file.
        string leftover = Path.Combine(_dir, "mail.emdb.compact");
        File.WriteAllText(leftover, "never renamed");
        File.Delete(leftover);

        var result = DirectoryFsync.SyncContainingDirectory(leftover);

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public void SyncContainingDirectory_ResolvesRelativePaths()
    {
        // A bare filename has no directory component; the helper must resolve
        // it against the current directory rather than failing on "".
        var result = DirectoryFsync.SyncContainingDirectory("bare-filename.emdb");

        Assert.True(result.IsSuccess, result.Error);
    }

    [Fact]
    public void SyncContainingDirectory_FilesystemRoot_FailsWithClearError()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(_dir))!;

        var result = DirectoryFsync.SyncContainingDirectory(root);

        Assert.True(result.IsFailure);
        Assert.Contains("no containing directory", result.Error);
    }

    [Fact]
    public void SyncContainingDirectory_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DirectoryFsync.SyncContainingDirectory(null!));
        Assert.Throws<ArgumentException>(() => DirectoryFsync.SyncContainingDirectory(" "));
    }

    [Fact]
    public void SyncContainingDirectory_MissingParentDirectory_Fails()
    {
        string file = Path.Combine(_dir, "no-such-subdir", "file.emdb");

        var result = DirectoryFsync.SyncContainingDirectory(file);

        Assert.True(result.IsFailure);
        Assert.Contains(Path.Combine(_dir, "no-such-subdir"), result.Error);
    }

    // ---- Windows no-op contract ----

    [Fact]
    public void Sync_OnWindows_IsSuccessNoOp_EvenForMissingDirectory()
    {
        // On Windows the helper is a documented no-op (NTFS journals directory
        // metadata; there is no Win32 directory fsync), so it succeeds without
        // touching the path at all. On POSIX this asserts the inverse guard.
        string missing = Path.Combine(_dir, "definitely-missing");

        var result = DirectoryFsync.Sync(missing);

        if (OperatingSystem.IsWindows())
            Assert.True(result.IsSuccess, result.Error);
        else
            Assert.True(result.IsFailure);
    }
}
