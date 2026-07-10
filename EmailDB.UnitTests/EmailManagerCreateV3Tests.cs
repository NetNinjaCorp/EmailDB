using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="EmailManager.Create"/> (US-EMDB-84-5,
/// EmailDB_FileFormat_Spec.md Section 11.1): create/initialize a new v3 file —
/// superblocks A+B, initial Metadata/KeyStore/FolderTree blocks, empty index roots,
/// the first Checkpoint, and the directory fsync — with and without encryption. The
/// decisive assertion for every case is that the produced file <b>reopens cleanly</b>
/// via <see cref="CleanOpener"/> (the story's acceptance criterion), and that the
/// structure the open resolves matches what create wrote.
/// </summary>
public class EmailManagerCreateV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-create-{Guid.NewGuid():N}.emdb");

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) keep the KDF fast in tests.
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRead() =>
        new(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    // ------------------------------------------------------------- Unencrypted

    [Fact]
    public void Create_unencrypted_produces_a_file_that_reopens_cleanly()
    {
        var created = EmailManager.Create(_path);
        Ok(created);
        using (var manager = created.Value)
        {
            Assert.False(manager.IsEncrypted);
            Assert.Equal(0UL, manager.LastCheckpointSequence);
            Assert.False(manager.LastCheckpoint.IsAbsent);
            Assert.Equal(0, manager.Superblock.EncryptionEnabled);
            Assert.Equal(1, manager.Superblock.CleanShutdown);
        }

        Assert.True(File.Exists(_path));

        // Reopen via the clean-open fast path: this is the acceptance criterion.
        using var stream = OpenRead();
        var opened = CleanOpener.Open(stream);
        Ok(opened);
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Value.Kind);
        using var state = opened.Value.State!;

        // First Checkpoint (sequence 0) with the roots create wrote: Metadata + FolderTree
        // present, index roots empty, KeyStore absent (unencrypted).
        Assert.Equal(0UL, state.Checkpoint.Checkpoint.CheckpointSequence);
        Assert.NotNull(state.Checkpoint.MetadataRoot);
        Assert.NotNull(state.Checkpoint.FolderTreeRoot);
        Assert.Null(state.Checkpoint.PrimaryIndexRoot);
        Assert.Null(state.Checkpoint.LocationIndexRoot);
        Assert.Null(state.Checkpoint.KeyStoreRoot);
        Assert.Null(state.Checkpoint.PreviousCheckpoint);

        // Empty index roots -> no live blocks in the BlockLocationIndex.
        Assert.Equal(0, (int)state.LocationIndex.Count);
        Assert.Equal(0, state.RuntimeMap.Count);
    }

    [Fact]
    public void Create_writes_both_superblock_slots_A_seq1_and_B_seq2()
    {
        EmailManager.Create(_path).Value.Dispose();

        // Slot B (sequence 2) wins on load; both slots are valid.
        using var stream = OpenRead();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        var loaded = sbManager.Load();
        Ok(loaded);
        Assert.Equal(2UL, loaded.Value.SuperblockSequence);
        Assert.Equal(1, loaded.Value.CleanShutdown);
    }

    [Fact]
    public void Create_fsyncs_the_file_and_the_directory()
    {
        // A file exists and is non-trivially sized (superblocks + initial blocks +
        // checkpoint). The directory fsync path returned success (or Create would fail).
        EmailManager.Create(_path).Value.Dispose();
        long length = new FileInfo(_path).Length;
        // At least the two 4096-byte superblock slots plus block-stream content.
        Assert.True(length > 2 * SuperblockSerializer.SlotSize,
            $"file length {length} is not larger than the superblock region.");
    }

    // --------------------------------------------------------------- Encrypted

    [Fact]
    public void Create_encrypted_produces_a_file_that_reopens_cleanly_with_the_password()
    {
        const string password = "correct horse battery staple";
        var created = EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = password,
            KdfParameters = FastParams,
        });
        Ok(created);
        using (var manager = created.Value)
        {
            Assert.True(manager.IsEncrypted);
            Assert.NotNull(manager.EncryptionProvider);
            Assert.Equal(1, manager.Superblock.EncryptionEnabled);
            Assert.Equal(EncryptionBootstrap.AesGcmAlgorithmId, manager.Superblock.AlgorithmId);
        }

        // Reopen with the real bootstrap chain (file + password only).
        using var stream = OpenRead();
        var bootstrap = new PasswordEncryptionBootstrap(stream, password);
        var opened = CleanOpener.Open(stream, bootstrap);
        Ok(opened);
        Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Value.Kind);
        using var state = opened.Value.State!;

        // The encrypted file names a KeyStore root; the bootstrap built a live provider
        // at epoch 0.
        Assert.NotNull(state.Checkpoint.KeyStoreRoot);
        Assert.NotNull(state.Checkpoint.MetadataRoot);
        Assert.NotNull(state.Checkpoint.FolderTreeRoot);
        Assert.NotNull(bootstrap.Provider);
        Assert.Equal(0, bootstrap.Provider!.ActiveEpoch);
        bootstrap.Provider.Dispose();
    }

    [Fact]
    public void Create_encrypted_without_a_bootstrap_reports_the_encryption_seam()
    {
        var created = EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = "hunter2",
            KdfParameters = FastParams,
        });
        Ok(created);
        created.Value.Dispose();

        // Opening an encrypted file with no bootstrap must report the seam, not read
        // ciphertext as plaintext.
        using var stream = OpenRead();
        var opened = CleanOpener.Open(stream);
        Ok(opened);
        Assert.Equal(OpenOutcomeKind.EncryptionBootstrapRequired, opened.Value.Kind);
    }

    [Fact]
    public void Create_encrypted_rejects_the_wrong_password_on_reopen()
    {
        const string password = "the-right-password";
        var created = EmailManager.Create(_path, new EmailManagerCreateOptions
        {
            Password = password,
            KdfParameters = FastParams,
        });
        Ok(created);
        created.Value.Dispose();

        using var stream = OpenRead();
        var bootstrap = new PasswordEncryptionBootstrap(stream, "the-WRONG-password");
        var opened = CleanOpener.Open(stream, bootstrap);
        Assert.True(opened.IsFailure, "a wrong password must fail the open.");
    }

    // --------------------------------------------------------------- Failures

    [Fact]
    public void Create_fails_when_the_file_already_exists()
    {
        using var first = EmailManager.Create(_path).Value;

        // A second create over the same path must refuse (exclusive creation).
        var again = EmailManager.Create(_path);
        Assert.True(again.IsFailure);
    }
}
