using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for O(1) password change (<see cref="EncryptionBootstrap.ChangePassword"/>, task
/// US-EMDB-79-6, docs/Encryption.md Section 5). A password change re-wraps the DEK table under a
/// KEK derived from the new password: it decrypts the KeyStore with the old KEK, mints a fresh
/// salt (optionally upgrading the Argon2id parameters), derives the new KEK, and appends ONE
/// re-sealed KeyStore block — the DEKs never change and no data block is rewritten. The single
/// superblock write that repoints Salt/KdfParams/token/pointer is the crash-safe commit point.
/// Covers:
/// <list type="bullet">
/// <item>the change writes exactly one KeyStore block and one superblock update, with every data
/// block byte-identical (zero data blocks touched);</item>
/// <item>a crash before the superblock write (skip ApplyTo) leaves the old password fully working
/// and the new one rejected;</item>
/// <item>after completion the new password opens and the old is rejected with the distinct
/// TokenMismatch error;</item>
/// <item>the DEKs are unchanged across the change — old-epoch blocks stay readable;</item>
/// <item>an optional KdfParams upgrade is honored and load-bearing.</item>
/// </list>
/// Uses minimal Argon2id costs so key derivation is fast.
/// </summary>
public class ChangePasswordV3Tests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-changepw-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xD0 + i)).ToArray();

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) — keeps the KDF fast in tests.
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private void WriteSuperblock(Action<Superblock> configure)
    {
        using var stream = OpenRW();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        Superblock Make()
        {
            var sb = new Superblock { FileId = (byte[])FileId.Clone(), CleanShutdown = 1 };
            configure(sb);
            return sb;
        }
        Ok(sbManager.Write(Make()));
        Ok(sbManager.Write(Make())); // fill both slots
    }

    private Superblock LoadSuperblock()
    {
        using var stream = OpenRW();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        var loaded = sbManager.Load();
        Ok(loaded);
        return loaded.Value;
    }

    // Persists an already-populated superblock (e.g. after PasswordChangeOutcome.ApplyTo) to both slots.
    private void PersistSuperblock(Superblock sb)
    {
        using var stream = OpenRW();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        Ok(sbManager.Write(sb.Clone()));
        Ok(sbManager.Write(sb.Clone())); // fill both slots
    }

    // Creates an encrypted file with one data block, returning its offset and plaintext.
    private (Superblock Superblock, long DataOffset, byte[] Plaintext) CreateEncryptedFileWithData(string password)
    {
        var plaintext = "the data block that must survive the password change untouched"u8.ToArray();
        EncryptionBootstrap.CreatedEncryption created;
        long dataOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;

            var store = new EncryptedBlockStore(manager, created.Provider);
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintext);
            Ok(appended);
            dataOffset = appended.Value.Offset;
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);
        return (LoadSuperblock(), dataOffset, plaintext);
    }

    private static byte[] ReadAllBytes(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[s.Length];
        s.ReadExactly(buf);
        return buf;
    }

    // Performs one committed password change: re-seals the KeyStore under newPassword, flushes the
    // block, applies the new credential fields to `superblock`, and persists the superblock to disk.
    private void CommitChange(Superblock superblock, string oldPassword, string newPassword)
    {
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, newPassword, manager);
            Ok(changed);
            Ok(manager.Flush());
            changed.Value.Provider.Dispose();
            changed.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// A password change appends exactly ONE KeyStore block and repoints the superblock, with every
    /// pre-existing block (the data block AND the original KeyStore) byte-identical afterward: the
    /// change is append-only over the block region and touches zero data blocks.
    /// </summary>
    [Fact]
    public void ChangePassword_AppendsOneKeyStoreBlock_AndTouchesNoDataBlocks()
    {
        const string oldPassword = "the original password";
        const string newPassword = "the replacement password";

        var (superblock, dataOffset, _) = CreateEncryptedFileWithData(oldPassword);
        long oldKeyStoreOffset = superblock.ActiveKeyStoreOffset;

        // Snapshot the whole BLOCK region (everything after the superblock slots, up to EOF). Data
        // blocks and the original KeyStore live here; the change is append-only over this region, so
        // it must be byte-identical afterward. The superblock region [0, RegionSize) is excluded
        // because the committing superblock write is the ONE expected mutation.
        long blockRegionStart = SuperblockManager.SuperblockRegionSize;
        long prefixLength = new FileInfo(_path).Length;
        byte[] fileBefore = ReadAllBytes(_path);
        byte[] blockRegionBefore = fileBefore.AsSpan((int)blockRegionStart, (int)(prefixLength - blockRegionStart)).ToArray();
        Assert.True(dataOffset >= blockRegionStart, "the data block must live in the block region, after the superblock slots");

        long newKeyStoreOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, newPassword, manager);
            Ok(changed);
            using var provider = changed.Value.Provider;

            newKeyStoreOffset = changed.Value.KeyStore.Offset;
            // Exactly one new KeyStore block was appended: its pointer differs from the old one and
            // lands at the previous end-of-file (append-only, nothing rewritten before it).
            Assert.NotEqual(oldKeyStoreOffset, newKeyStoreOffset);
            Assert.Equal(prefixLength, newKeyStoreOffset);

            changed.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);

        // Every byte of the original block region (data block + original KeyStore) is unchanged.
        byte[] fullAfter = ReadAllBytes(_path);
        Assert.True(fullAfter.Length > prefixLength, "the change must have appended a KeyStore block");
        byte[] blockRegionAfterSamePrefix =
            fullAfter.AsSpan((int)blockRegionStart, (int)(prefixLength - blockRegionStart)).ToArray();
        Assert.True(blockRegionBefore.AsSpan().SequenceEqual(blockRegionAfterSamePrefix),
            "the pre-existing block region must be byte-identical after a password change");

        // The superblock now points at the new KeyStore block, not the old one.
        var reloaded = LoadSuperblock();
        Assert.Equal(newKeyStoreOffset, reloaded.ActiveKeyStoreOffset);
        Assert.NotEqual(oldKeyStoreOffset, reloaded.ActiveKeyStoreOffset);
    }

    /// <summary>
    /// Crash simulation: perform the change but DO NOT persist the superblock (skip ApplyTo, drop
    /// the new pointer/salt/token). The file on disk still carries the OLD Salt/KdfParams/token and
    /// the OLD KeyStore pointer, so the OLD password still opens it and the NEW password is rejected.
    /// </summary>
    [Fact]
    public void CrashBeforeSuperblockWrite_LeavesOldPasswordWorking_AndNewRejected()
    {
        const string oldPassword = "old password before crash";
        const string newPassword = "new password never committed";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(oldPassword);

        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, newPassword, manager);
            Ok(changed);
            Ok(manager.Flush());
            // CRASH: the new KeyStore block is on disk, but we never call ApplyTo / persist the
            // superblock. Dispose the provider and walk away — the commit point never happened.
            changed.Value.Provider.Dispose();
        }
        // Deliberately NOT persisting the superblock — it still points at the old KeyStore.

        var reloaded = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);

        // OLD password still opens the file and decrypts the data — the change was not committed.
        var openedOld = EncryptionBootstrap.Open(reloaded, oldPassword, reopenManager);
        Ok(openedOld);
        using (var providerOld = openedOld.Value)
        {
            var store = new EncryptedBlockStore(reopenManager, providerOld);
            var decrypted = store.ReadDecrypted(dataOffset);
            Ok(decrypted);
            Assert.Equal(plaintext, decrypted.Value);
        }

        // NEW password is rejected against the un-committed superblock (it still holds the old token).
        var openedNew = EncryptionBootstrap.Open(reloaded, newPassword, reopenManager);
        Assert.True(openedNew.IsFailure);
        var tamper = Assert.IsType<WrongKeyOrTamperError>(openedNew.VerificationError);
        Assert.Equal(TamperCause.TokenMismatch, tamper.Cause);
    }

    /// <summary>
    /// Crash-before-commit leaves the old password not just openable but FULLY operational. After the
    /// re-sealed KeyStore block hits disk yet the superblock is never persisted (skip ApplyTo), the
    /// file is recovered STRICTLY from its on-disk superblock bytes and, under the surviving old
    /// password: (a) it opens and the pre-existing data decrypts; (b) a brand-new encrypted block can
    /// be written; and (c) a SECOND password change succeeds and commits — after which the final
    /// password opens and decrypts both the original and the post-crash block, and the old password is
    /// retired. Meanwhile the password from the un-committed change is rejected. This proves normal
    /// operation continues seamlessly across the aborted change, not merely that reads work once.
    /// </summary>
    [Fact]
    public void CrashBeforeSuperblockWrite_OldPasswordFullyOperational_NewWritesAndSecondChangeSucceed()
    {
        const string oldPassword = "durable old password";
        const string uncommittedPassword = "password lost to the crash";
        const string finalPassword = "password committed after recovery";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(oldPassword);

        // CRASH: change to uncommittedPassword, flush the new KeyStore block, but NEVER persist the
        // superblock (skip ApplyTo). Walk away — the commit point never happened.
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, uncommittedPassword, manager);
            Ok(changed);
            Ok(manager.Flush());
            changed.Value.Provider.Dispose();
        }

        // Durability: recover the superblock from DISK BYTES (not the in-memory object). It still
        // carries the old Salt/KdfParams/token/pointer.
        var recovered = LoadSuperblock();

        // (b) The un-committed new password is rejected against the recovered superblock.
        using (var probeStream = OpenRW())
        using (var probeManager = new BlockManager(
            probeStream, maxPayloadLength: recovered.MaxPayloadLength, ownsStream: false))
        {
            var rejected = EncryptionBootstrap.Open(recovered, uncommittedPassword, probeManager);
            Assert.True(rejected.IsFailure);
            Assert.Equal(TamperCause.TokenMismatch,
                Assert.IsType<WrongKeyOrTamperError>(rejected.VerificationError).Cause);
        }

        // (a) The old password FULLY works: open, decrypt the existing block, AND write a new one.
        byte[] postCrashPlain = "a fresh block written under the surviving old password"u8.ToArray();
        long postCrashOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: recovered.MaxPayloadLength, ownsStream: false))
        {
            var opened = EncryptionBootstrap.Open(recovered, oldPassword, manager);
            Ok(opened);
            using var provider = opened.Value;
            var store = new EncryptedBlockStore(manager, provider);

            var decrypted = store.ReadDecrypted(dataOffset);
            Ok(decrypted);
            Assert.Equal(plaintext, decrypted.Value);

            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, postCrashPlain);
            Ok(appended);
            postCrashOffset = appended.Value.Offset;
            Ok(manager.Flush());
        }

        // (c) Continued operation: a SECOND password change (old -> final) now succeeds and commits.
        var sbForChange = LoadSuperblock();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: sbForChange.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(sbForChange, oldPassword, finalPassword, manager);
            Ok(changed);
            Ok(manager.Flush());
            changed.Value.Provider.Dispose();
            changed.Value.ApplyTo(sbForChange);
        }
        PersistSuperblock(sbForChange);

        // The committed final password opens and decrypts BOTH the original and the post-crash block.
        var finalSb = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: finalSb.MaxPayloadLength, ownsStream: false);
        var openedFinal = EncryptionBootstrap.Open(finalSb, finalPassword, reopenManager);
        Ok(openedFinal);
        using (var providerFinal = openedFinal.Value)
        {
            var readStore = new EncryptedBlockStore(reopenManager, providerFinal);
            var d0 = readStore.ReadDecrypted(dataOffset);
            Ok(d0);
            Assert.Equal(plaintext, d0.Value);
            var d1 = readStore.ReadDecrypted(postCrashOffset);
            Ok(d1);
            Assert.Equal(postCrashPlain, d1.Value);
        }

        // The old password is retired by the committed second change.
        var openedOld = EncryptionBootstrap.Open(finalSb, oldPassword, reopenManager);
        Assert.True(openedOld.IsFailure);
        Assert.Equal(TamperCause.TokenMismatch,
            Assert.IsType<WrongKeyOrTamperError>(openedOld.VerificationError).Cause);
    }

    /// <summary>
    /// The KeyStore-then-superblock commit ordering is crash-safe for key ROTATION too (same
    /// <c>ApplyTo</c> commit shape as a password change, docs/Encryption.md Section 5). After a
    /// rotation's new KeyStore block (epoch 1) hits disk but the superblock is never persisted (skip
    /// ApplyTo), recovery from the on-disk superblock bytes leaves the OLD active epoch (0) fully in
    /// force: the file opens, its epoch-0 data decrypts, <see cref="EpochDekProvider.ActiveEpoch"/> is
    /// still 0 (the un-committed epoch-1 DEK is absent), new writes go under epoch 0, and a SECOND
    /// rotation then succeeds and commits cleanly to epoch 1 — proving operation continues across the
    /// aborted rotation.
    /// </summary>
    [Fact]
    public void CrashBeforeSuperblockWrite_AfterRotate_OldEpochStaysActive_AndOperationContinues()
    {
        const string password = "the rotation credential";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(password);
        long keyStoreBefore = superblock.ActiveKeyStoreOffset;

        // CRASH: rotate to epoch 1, flush the new KeyStore block, but NEVER persist the superblock.
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var rotated = EncryptionBootstrap.RotateKey(superblock, password, manager);
            Ok(rotated);
            Assert.Equal((ushort)1, rotated.Value.NewEpoch);
            Ok(manager.Flush());
            rotated.Value.Provider.Dispose();
        }

        // Recover from disk bytes: the superblock still points at the OLD KeyStore (old epoch 0).
        var recovered = LoadSuperblock();
        Assert.Equal(keyStoreBefore, recovered.ActiveKeyStoreOffset);

        byte[] postCrashPlain = "written under the surviving pre-rotation epoch"u8.ToArray();
        long postCrashOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: recovered.MaxPayloadLength, ownsStream: false))
        {
            var opened = EncryptionBootstrap.Open(recovered, password, manager);
            Ok(opened);
            using var provider = opened.Value;
            // The old active epoch (0) survives; the un-committed rotation's epoch-1 DEK is absent.
            Assert.Equal((ushort)0, provider.ActiveEpoch);
            Assert.False(provider.HasLiveDek(1), "the un-committed rotation's epoch-1 DEK must not be present");

            var store = new EncryptedBlockStore(manager, provider);
            var decrypted = store.ReadDecrypted(dataOffset);
            Ok(decrypted);
            Assert.Equal(plaintext, decrypted.Value);

            // Continued operation: a new write goes under the surviving epoch 0.
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, postCrashPlain);
            Ok(appended);
            postCrashOffset = appended.Value.Offset;
            Ok(manager.Flush());
            Assert.Equal((ushort)0, manager.Read(postCrashOffset).Value.Header.KeyEpoch);
        }

        // Continued operation: rotating AGAIN now succeeds and commits cleanly to epoch 1.
        var sbForRotate = LoadSuperblock();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: sbForRotate.MaxPayloadLength, ownsStream: false))
        {
            var rotated = EncryptionBootstrap.RotateKey(sbForRotate, password, manager);
            Ok(rotated);
            Assert.Equal((ushort)1, rotated.Value.NewEpoch);
            Ok(manager.Flush());
            rotated.Value.Provider.Dispose();
            rotated.Value.ApplyTo(sbForRotate);
        }
        PersistSuperblock(sbForRotate);

        // The committed rotation is active on disk; both epoch-0 blocks still decrypt.
        var finalSb = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: finalSb.MaxPayloadLength, ownsStream: false);
        var openedFinal = EncryptionBootstrap.Open(finalSb, password, reopenManager);
        Ok(openedFinal);
        using var providerFinal = openedFinal.Value;
        Assert.Equal((ushort)1, providerFinal.ActiveEpoch);
        var readStore = new EncryptedBlockStore(reopenManager, providerFinal);
        var d0 = readStore.ReadDecrypted(dataOffset);
        Ok(d0);
        Assert.Equal(plaintext, d0.Value);
        var d1 = readStore.ReadDecrypted(postCrashOffset);
        Ok(d1);
        Assert.Equal(postCrashPlain, d1.Value);
    }

    /// <summary>
    /// After a committed password change, the NEW password opens the file (and decrypts data) while
    /// the OLD password is rejected with the distinct <see cref="TamperCause.TokenMismatch"/> signal.
    /// </summary>
    [Fact]
    public void AfterCompletion_NewPasswordOpens_OldPasswordRejectedWithTokenMismatch()
    {
        const string oldPassword = "password to be retired";
        const string newPassword = "password now in force";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(oldPassword);

        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, newPassword, manager);
            Ok(changed);
            Ok(manager.Flush());
            changed.Value.Provider.Dispose();
            changed.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);

        var reloaded = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);

        // NEW password opens and decrypts the data block.
        var openedNew = EncryptionBootstrap.Open(reloaded, newPassword, reopenManager);
        Ok(openedNew);
        using (var providerNew = openedNew.Value)
        {
            var store = new EncryptedBlockStore(reopenManager, providerNew);
            var decrypted = store.ReadDecrypted(dataOffset);
            Ok(decrypted);
            Assert.Equal(plaintext, decrypted.Value);
        }

        // OLD password is now rejected with the distinct token-mismatch cause.
        var openedOld = EncryptionBootstrap.Open(reloaded, oldPassword, reopenManager);
        Assert.True(openedOld.IsFailure);
        var tamper = Assert.IsType<WrongKeyOrTamperError>(openedOld.VerificationError);
        Assert.Equal(TamperCause.TokenMismatch, tamper.Cause);
    }

    /// <summary>
    /// After a committed change, the OLD password is rejected by the KeyVerificationToken fast-fail
    /// BEFORE the KeyStore block is ever read (docs/Encryption.md Section 2 step 3, EncryptionBootstrap
    /// step "surfaced before the KeyStore is touched"). Proven by sabotaging the superblock's KeyStore
    /// pointer to aim at the (readable, but non-KeyStore) data block: the OLD password still fails with
    /// the distinct <see cref="TamperCause.TokenMismatch"/> — it never looks at the sabotaged pointer —
    /// while the NEW password passes the token and only THEN trips over the wrong block type at the
    /// sabotaged offset. The contrast pins the token check strictly upstream of, and independent of,
    /// the KeyStore access, so a wrong-password rejection does no KeyStore work.
    /// </summary>
    [Fact]
    public void AfterCompletion_OldPasswordRejectedAtTokenCheck_BeforeTheKeyStoreIsRead()
    {
        const string oldPassword = "retired credential";
        const string newPassword = "credential in force";

        var (superblock, dataOffset, _) = CreateEncryptedFileWithData(oldPassword);
        CommitChange(superblock, oldPassword, newPassword);

        var reloaded = LoadSuperblock();
        // Sabotage the KeyStore pointer so it aims at the data block, which reads fine but is NOT a
        // KeyStore. Any open that reaches the KeyStore step will trip over the wrong block type; an
        // open rejected at the token never gets there.
        reloaded.ActiveKeyStoreOffset = dataOffset;

        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);

        // OLD password: rejected at the token with TokenMismatch — the sabotaged pointer is never used.
        var openedOld = EncryptionBootstrap.Open(reloaded, oldPassword, reopenManager);
        Assert.True(openedOld.IsFailure);
        Assert.Equal(TamperCause.TokenMismatch,
            Assert.IsType<WrongKeyOrTamperError>(openedOld.VerificationError).Cause);

        // NEW password: passes the token, THEN reaches the KeyStore read and fails on the wrong block
        // type at the sabotaged offset — a plain (non-tamper) failure. This proves the token check is
        // upstream of the KeyStore access, so the OLD-password rejection above did no KeyStore work.
        var openedNew = EncryptionBootstrap.Open(reloaded, newPassword, reopenManager);
        Assert.True(openedNew.IsFailure);
        Assert.Null(openedNew.VerificationError);
        Assert.Contains("not a KeyStore block", openedNew.Error);
    }

    /// <summary>
    /// Repeated changes: after original → interim → original (two committed changes returning to the
    /// starting password), the ORIGINAL password opens the file again and decrypts the pre-change data,
    /// while the now-superseded INTERIM password is rejected with <see cref="TamperCause.TokenMismatch"/>.
    /// Each change mints a fresh salt/token, so "back to the original" is a genuine new credential that
    /// happens to share the original's spelling — the file still opens under it and the interim is retired.
    /// </summary>
    [Fact]
    public void ChangingBackToTheOriginalPasswordLater_Works_AndTheInterimIsRejected()
    {
        const string original = "the original password";
        const string interim = "the interim password";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(original);

        CommitChange(superblock, original, interim);        // original -> interim
        var afterFirst = LoadSuperblock();
        CommitChange(afterFirst, interim, original);        // interim -> original (back to the start)

        var finalSb = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: finalSb.MaxPayloadLength, ownsStream: false);

        // The original password opens again and decrypts the pre-change data.
        var openedOriginal = EncryptionBootstrap.Open(finalSb, original, reopenManager);
        Ok(openedOriginal);
        using (var provider = openedOriginal.Value)
        {
            var store = new EncryptedBlockStore(reopenManager, provider);
            var decrypted = store.ReadDecrypted(dataOffset);
            Ok(decrypted);
            Assert.Equal(plaintext, decrypted.Value);
        }

        // The interim password is retired by the second change.
        var openedInterim = EncryptionBootstrap.Open(finalSb, interim, reopenManager);
        Assert.True(openedInterim.IsFailure);
        Assert.Equal(TamperCause.TokenMismatch,
            Assert.IsType<WrongKeyOrTamperError>(openedInterim.VerificationError).Cause);
    }

    /// <summary>
    /// Edge case: a password change where the new password equals the old one is still a real re-seal.
    /// It mints a FRESH salt and a NEW token (both differ from the pre-change values), yet the (same)
    /// password still opens the file and decrypts the pre-change data. The refreshed token is bound to
    /// the fresh salt — a KEK derived from the OLD salt does not open it — so the change genuinely
    /// rewrapped the KeyStore rather than being a no-op.
    /// </summary>
    [Fact]
    public void ChangePassword_WhereNewEqualsOld_RefreshesSaltAndToken_AndStillOpens()
    {
        const string password = "identical in spelling, refreshed in salt";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(password);
        byte[] oldSalt = (byte[])superblock.Salt.Clone();
        byte[] oldToken = (byte[])superblock.KeyVerificationToken.Clone();

        CommitChange(superblock, password, password);

        var reloaded = LoadSuperblock();
        // Even with an identical password string, the change minted a fresh salt and a new token.
        Assert.False(reloaded.Salt.AsSpan().SequenceEqual(oldSalt),
            "a fresh salt must be minted even when new == old");
        Assert.False(reloaded.KeyVerificationToken.AsSpan().SequenceEqual(oldToken),
            "a fresh token must be minted even when new == old");

        // The refreshed token is bound to the FRESH salt: a KEK from the OLD salt does not open it.
        var kekOldSalt = PasswordKeyDerivation.DeriveKek(
            password, Argon2idParams.Unpack(reloaded.KdfParams), oldSalt);
        Assert.False(KeyVerificationToken.TryVerify(reloaded.KeyVerificationToken, kekOldSalt),
            "the refreshed token must be bound to the fresh salt, not the old one");

        // The (same) password still opens the file and decrypts the pre-change data.
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(reloaded, password, reopenManager);
        Ok(opened);
        using var providerAfter = opened.Value;
        var readStore = new EncryptedBlockStore(reopenManager, providerAfter);
        var decrypted = readStore.ReadDecrypted(dataOffset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);
    }

    /// <summary>
    /// The DEKs are carried across the change UNCHANGED: a block sealed under epoch 0 with the old
    /// password is still decryptable with the new password after reopening from disk. Also proves a
    /// wrong OLD password is rejected before any write (no state change).
    /// </summary>
    [Fact]
    public void DeksUnchangedAcrossChange_OldEpochBlockStillReadable_AndWrongOldPasswordRefused()
    {
        const string oldPassword = "original credential";
        const string newPassword = "rotated credential";

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(oldPassword);

        // A change attempted with the WRONG old password must fail fast and change nothing.
        long lengthBeforeWrong = new FileInfo(_path).Length;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var wrong = EncryptionBootstrap.ChangePassword(superblock, "not the old password", newPassword, manager);
            Assert.True(wrong.IsFailure, "a change with the wrong current password must be refused");
            var tamper = Assert.IsType<WrongKeyOrTamperError>(wrong.VerificationError);
            Assert.Equal(TamperCause.TokenMismatch, tamper.Cause);
        }
        Assert.Equal(lengthBeforeWrong, new FileInfo(_path).Length);

        // Now the real change with the correct old password.
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, newPassword, manager);
            Ok(changed);

            // The provider returned from the change is at the same active epoch (0) and still holds
            // the SAME DEK — it decrypts the block that was written under the old password.
            using var provider = changed.Value.Provider;
            Assert.Equal((ushort)0, provider.ActiveEpoch);
            Assert.True(provider.HasLiveDek(0));
            var storeNow = new EncryptedBlockStore(manager, provider);
            var decryptedNow = storeNow.ReadDecrypted(dataOffset);
            Ok(decryptedNow);
            Assert.Equal(plaintext, decryptedNow.Value);

            Ok(manager.Flush());
            changed.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);

        // Reopen from disk with the new password: the epoch-0 block still decrypts (DEK unchanged).
        var reloaded = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(reloaded, newPassword, reopenManager);
        Ok(opened);
        using var providerAfter = opened.Value;
        Assert.Equal((ushort)0, providerAfter.ActiveEpoch);
        Assert.True(providerAfter.HasLiveDek(0), "the epoch-0 DEK must survive the password change");
        Assert.Equal((ushort)0, reopenManager.Read(dataOffset).Value.Header.KeyEpoch);

        var readStore = new EncryptedBlockStore(reopenManager, providerAfter);
        var decrypted = readStore.ReadDecrypted(dataOffset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);
    }

    /// <summary>
    /// The change is O(1) in the data (docs/Encryption.md Section 5): with a file carrying MANY data
    /// blocks of DIFFERENT types and policies — plaintext Metadata, plaintext BTreeLeaf (Default
    /// policy), encrypted EmailContent/EmailMetadata — AND blocks written under a SECOND key epoch
    /// after a rotation (so the pre-existing region also holds an earlier KeyStore block), a password
    /// change appends EXACTLY ONE KeyStore block: file growth equals precisely that block's on-disk
    /// <see cref="BlockLocation.TotalBlockLength"/> (nothing else is written to the block region) and
    /// every pre-existing byte is unchanged. Running it against two very different data-block counts
    /// and asserting the growth is IDENTICAL proves the write cost does not scale with data size. The
    /// reopen decrypts an epoch-0 AND an epoch-1 block, proving every DEK survived unchanged.
    /// </summary>
    [Fact]
    public void ChangePassword_ManyMixedMultiEpochBlocks_AppendsExactlyOneKeyStoreBlock_GrowthIndependentOfData()
    {
        const string oldPassword = "credential guarding a large file";
        const string newPassword = "the rotated credential";

        // Two files that differ only in how many data blocks they carry (same 2-epoch KeyStore).
        long growthSmall = MeasurePasswordChangeGrowth(dataBlockCount: 3, oldPassword, newPassword);
        long growthLarge = MeasurePasswordChangeGrowth(dataBlockCount: 60, oldPassword, newPassword);

        // O(1): the change appended one KeyStore block of the SAME size in both files — the write
        // cost is independent of how many data blocks the file holds. (Each run also asserts, inside
        // the helper, that file growth equals exactly that one block's TotalBlockLength and that the
        // entire pre-existing block region is byte-identical.)
        Assert.Equal(growthSmall, growthLarge);
    }

    /// <summary>
    /// Builds an encrypted file at a fresh path carrying <paramref name="dataBlockCount"/> mixed-type,
    /// mixed-policy data blocks plus blocks under two key epochs (via a rotation), performs a password
    /// change, and asserts the change appended exactly ONE KeyStore block at EOF, touched zero data
    /// blocks (byte-identical pre-existing region), repointed the superblock (one update), and left
    /// both epochs' data decryptable. Returns the file growth in bytes (== the appended KeyStore
    /// block's TotalBlockLength) so the caller can prove it is independent of the data-block count.
    /// </summary>
    private long MeasurePasswordChangeGrowth(int dataBlockCount, string oldPassword, string newPassword)
    {
        string path = Path.Combine(Path.GetTempPath(), $"emaildb-changepw-o1-{Guid.NewGuid():N}.emdb");
        FileStream OpenPath() =>
            new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        void PersistSb(Superblock sb)
        {
            using var stream = OpenPath();
            using var sbManager = new SuperblockManager(stream, ownsStream: false);
            Ok(sbManager.Write(sb.Clone()));
            Ok(sbManager.Write(sb.Clone())); // fill both slots
        }
        Superblock LoadSb()
        {
            using var stream = OpenPath();
            using var sbManager = new SuperblockManager(stream, ownsStream: false);
            var loaded = sbManager.Load();
            Ok(loaded);
            return loaded.Value;
        }

        byte[] epoch0Plain = "epoch-0 content that must remain readable after the change"u8.ToArray();
        byte[] epoch1Plain = "epoch-1 content written after a key rotation, still readable"u8.ToArray();
        // Cycle several block types spanning both policy treatments: encrypted content/metadata and
        // plaintext Metadata / plaintext-under-Default BTreeLeaf.
        var mixedTypes = new[]
        {
            BlockType.EmailContent, BlockType.EmailMetadata, BlockType.Metadata, BlockType.BTreeLeaf,
        };

        try
        {
            // 1. Create encryption (epoch 0) and write the epoch-0 blocks, including many mixed ones.
            EncryptionBootstrap.CreatedEncryption created;
            long epoch0Offset;
            using (var stream = OpenPath())
            using (var manager = new BlockManager(stream, ownsStream: false))
            {
                var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, oldPassword, FastParams);
                Ok(createdResult);
                created = createdResult.Value;

                var store = new EncryptedBlockStore(manager, created.Provider); // Default policy
                var e0 = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, epoch0Plain);
                Ok(e0);
                epoch0Offset = e0.Value.Offset;
                for (int i = 0; i < dataBlockCount; i++)
                {
                    var payload = System.Text.Encoding.UTF8.GetBytes($"mixed data block {i} of the pre-existing region");
                    Ok(store.Append(mixedTypes[i % mixedTypes.Length], PayloadEncoding.RawBytes, payload));
                }
                Ok(manager.Flush());
                created.Provider.Dispose();
            }
            var superblock = new Superblock { FileId = (byte[])FileId.Clone(), CleanShutdown = 1 };
            created.ApplyTo(superblock);
            PersistSb(superblock);

            // 2. Rotate to epoch 1 (appends an earlier KeyStore block that must ALSO stay byte-identical
            //    across the later password change), then write encrypted blocks under the new epoch.
            superblock = LoadSb();
            long epoch1Offset;
            using (var stream = OpenPath())
            using (var manager = new BlockManager(
                stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
            {
                var rotated = EncryptionBootstrap.RotateKey(superblock, oldPassword, manager);
                Ok(rotated);
                using var rotProvider = rotated.Value.Provider;
                Assert.Equal((ushort)1, rotProvider.ActiveEpoch);
                var store = new EncryptedBlockStore(manager, rotProvider);
                var e1 = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, epoch1Plain);
                Ok(e1);
                epoch1Offset = e1.Value.Offset;
                Ok(store.Append(BlockType.EmailMetadata, PayloadEncoding.RawBytes, "more epoch-1 data"u8.ToArray()));
                Ok(manager.Flush());
                rotated.Value.ApplyTo(superblock);
            }
            PersistSb(superblock);

            // 3. Snapshot the whole pre-existing block region and EOF just before the password change.
            superblock = LoadSb();
            long oldKeyStoreOffset = superblock.ActiveKeyStoreOffset;
            long blockRegionStart = SuperblockManager.SuperblockRegionSize;
            long prefixLength = new FileInfo(path).Length;
            byte[] fileBefore = ReadAllBytes(path);
            byte[] blockRegionBefore =
                fileBefore.AsSpan((int)blockRegionStart, (int)(prefixLength - blockRegionStart)).ToArray();
            Assert.True(epoch0Offset >= blockRegionStart && epoch1Offset >= blockRegionStart,
                "the data blocks must live in the block region, after the superblock slots");

            // 4. Perform the password change.
            long newKeyStoreOffset, keyStoreBlockLength;
            using (var stream = OpenPath())
            using (var manager = new BlockManager(
                stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
            {
                var changed = EncryptionBootstrap.ChangePassword(superblock, oldPassword, newPassword, manager);
                Ok(changed);
                changed.Value.Provider.Dispose();

                newKeyStoreOffset = changed.Value.KeyStore.Offset;
                keyStoreBlockLength = changed.Value.KeyStore.TotalBlockLength;
                changed.Value.ApplyTo(superblock);
            }
            PersistSb(superblock);

            // 5. Exactly ONE KeyStore block appended at the old EOF, and the file grew by EXACTLY that
            //    block's on-disk size — nothing else was written to the block region.
            long newLength = new FileInfo(path).Length;
            long growth = newLength - prefixLength;
            Assert.Equal(prefixLength, newKeyStoreOffset);
            Assert.Equal(keyStoreBlockLength, growth);

            // Zero data blocks touched: every pre-existing byte (all data blocks of every type AND the
            // earlier KeyStore blocks) is byte-identical after the change.
            byte[] fileAfter = ReadAllBytes(path);
            byte[] blockRegionAfterSamePrefix =
                fileAfter.AsSpan((int)blockRegionStart, (int)(prefixLength - blockRegionStart)).ToArray();
            Assert.True(blockRegionBefore.AsSpan().SequenceEqual(blockRegionAfterSamePrefix),
                "the pre-existing block region must be byte-identical after a password change");

            // One superblock update: it now points at the newly appended KeyStore block.
            var reloaded = LoadSb();
            Assert.Equal(newKeyStoreOffset, reloaded.ActiveKeyStoreOffset);
            Assert.NotEqual(oldKeyStoreOffset, reloaded.ActiveKeyStoreOffset);

            // Every DEK survived unchanged: the new password decrypts an epoch-0 AND an epoch-1 block.
            using (var reopenStream = OpenPath())
            using (var reopenManager = new BlockManager(
                reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false))
            {
                var opened = EncryptionBootstrap.Open(reloaded, newPassword, reopenManager);
                Ok(opened);
                using var provider = opened.Value;
                var readStore = new EncryptedBlockStore(reopenManager, provider);
                Assert.Equal((ushort)0, reopenManager.Read(epoch0Offset).Value.Header.KeyEpoch);
                Assert.Equal((ushort)1, reopenManager.Read(epoch1Offset).Value.Header.KeyEpoch);
                var d0 = readStore.ReadDecrypted(epoch0Offset);
                Ok(d0);
                Assert.Equal(epoch0Plain, d0.Value);
                var d1 = readStore.ReadDecrypted(epoch1Offset);
                Ok(d1);
                Assert.Equal(epoch1Plain, d1.Value);
            }

            return growth;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// A password change may upgrade the Argon2id parameters: the new parameters are persisted and
    /// load-bearing (only they open the file, the old ones do not), while the fresh salt differs
    /// from the original.
    /// </summary>
    [Fact]
    public void ChangePassword_WithKdfUpgrade_PersistsAndHonorsNewParams_AndFreshSalt()
    {
        const string oldPassword = "before the kdf upgrade";
        const string newPassword = "after the kdf upgrade";
        // Stronger than FastParams (8/1/1) but still cheap enough for a test.
        var upgraded = new Argon2idParams(memoryKB: 64, iterations: 2, parallelism: 1);

        var (superblock, dataOffset, plaintext) = CreateEncryptedFileWithData(oldPassword);
        byte[] oldSalt = (byte[])superblock.Salt.Clone();
        byte[] oldKdfParams = (byte[])superblock.KdfParams.Clone();

        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(
                superblock, oldPassword, newPassword, manager, upgraded);
            Ok(changed);
            Ok(manager.Flush());
            changed.Value.Provider.Dispose();
            changed.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);

        var reloaded = LoadSuperblock();

        // The stored parameters are the upgraded ones, and the salt is fresh.
        var storedParams = Argon2idParams.Unpack(reloaded.KdfParams);
        Assert.Equal(upgraded.MemoryKB, storedParams.MemoryKB);
        Assert.Equal(upgraded.Iterations, storedParams.Iterations);
        Assert.Equal(upgraded.Parallelism, storedParams.Parallelism);
        Assert.False(reloaded.KdfParams.AsSpan().SequenceEqual(oldKdfParams), "KdfParams must have been upgraded");
        Assert.False(reloaded.Salt.AsSpan().SequenceEqual(oldSalt), "a fresh salt must be generated on password change");

        // The new params + new password are load-bearing; the OLD params do not open the token.
        var kekUpgraded = PasswordKeyDerivation.DeriveKek(newPassword, storedParams, reloaded.Salt);
        Assert.True(KeyVerificationToken.TryVerify(reloaded.KeyVerificationToken, kekUpgraded));
        var kekOldParams = PasswordKeyDerivation.DeriveKek(newPassword, FastParams, reloaded.Salt);
        Assert.False(KeyVerificationToken.TryVerify(reloaded.KeyVerificationToken, kekOldParams),
            "the pre-upgrade KDF params must not open a file that stored upgraded params");

        // And the data still decrypts under the new credential.
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(reloaded, newPassword, reopenManager);
        Ok(opened);
        using var provider = opened.Value;
        var store = new EncryptedBlockStore(reopenManager, provider);
        var decrypted = store.ReadDecrypted(dataOffset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);
    }
}
