using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 superblock open-time policy layer (EmailDB_FileFormat_Spec.md
/// Sections 3.3 and 10.2): slot validation on open, IncompatFlags refusal,
/// ReadOnlyCompatFlags read-only enforcement, CleanShutdown = 0 on first write /
/// = 1 on graceful close, and MaxPayloadLength exposure.
/// </summary>
public class SuperblockSessionTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-sbsession-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static Superblock CreateSuperblock() => new()
    {
        FileId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        ShardIndex = 7,
        CreatedTimestamp = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc).Ticks,
        CleanShutdown = 1,
    };

    /// <summary>Writes a superblock to a fresh file via the manager (the "previous session").</summary>
    private void SeedFile(Superblock superblock)
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        var written = manager.Write(superblock);
        Assert.True(written.IsSuccess, written.Error);
    }

    /// <summary>Loads the current (higher-valid-sequence) superblock straight from disk.</summary>
    private Superblock ReadCurrentFromDisk()
    {
        using var manager = new SuperblockManager(OpenStream(), ownsStream: true);
        var loaded = manager.Load();
        Assert.True(loaded.IsSuccess, loaded.Error);
        return loaded.Value;
    }

    /// <summary>Flips one byte inside the slot at the given offset (simulated torn write).</summary>
    private void CorruptSlot(long offset, int byteWithinSlot = 100)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(offset + byteWithinSlot, SeekOrigin.Begin);
        var b = (byte)fs.ReadByte();
        fs.Seek(offset + byteWithinSlot, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    // ---- DoD: slot validation on open (magic + checksum) wired into the open path ----

    [Fact]
    public void Open_ValidFile_LoadsSuperblockAndIsWritable()
    {
        SeedFile(CreateSuperblock());

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsSuccess, opened.Error);
        using var session = opened.Value;

        Assert.Equal(7U, session.Current.ShardIndex);
        Assert.False(session.IsReadOnly);
        Assert.True(session.WasCleanShutdown);
    }

    [Fact]
    public void Open_EmptyFile_FailsWithoutThrowing()
    {
        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsFailure);
        Assert.Contains("Cannot open", opened.Error);
    }

    [Fact]
    public void Open_BothSlotsCorrupt_FailsChecksumValidation()
    {
        SeedFile(CreateSuperblock());                       // slot A, seq 1
        CorruptSlot(SuperblockManager.SlotAOffset);         // bad checksum (magic intact)

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsFailure);
        Assert.Contains("checksum", opened.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_BadMagic_FailsMagicValidation()
    {
        SeedFile(CreateSuperblock());
        CorruptSlot(SuperblockManager.SlotAOffset, byteWithinSlot: 0); // flip a magic byte

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsFailure);
        Assert.Contains("magic", opened.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_PicksHigherValidSequence_WhenBothSlotsValid()
    {
        using (var manager = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            var first = CreateSuperblock();
            first.ShardIndex = 1;
            Assert.True(manager.Write(first).IsSuccess);    // slot A, seq 1
            var second = CreateSuperblock();
            second.ShardIndex = 2;
            Assert.True(manager.Write(second).IsSuccess);   // slot B, seq 2
        }

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsSuccess, opened.Error);
        using var session = opened.Value;
        Assert.Equal(2UL, session.Current.SuperblockSequence); // higher valid sequence wins
        Assert.Equal(2U, session.Current.ShardIndex);
    }

    [Fact]
    public void Open_PicksHigherValidSequence_WhenNewerSlotIsTorn()
    {
        using (var manager = new SuperblockManager(OpenStream(), ownsStream: true))
        {
            var first = CreateSuperblock();
            first.ShardIndex = 1;
            Assert.True(manager.Write(first).IsSuccess);    // slot A, seq 1
            var second = CreateSuperblock();
            second.ShardIndex = 2;
            Assert.True(manager.Write(second).IsSuccess);   // slot B, seq 2
        }
        CorruptSlot(SuperblockManager.SlotBOffset);         // tear the newer slot

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsSuccess, opened.Error);
        using var session = opened.Value;
        Assert.Equal(1UL, session.Current.SuperblockSequence); // survives on previous good slot
        Assert.Equal(1U, session.Current.ShardIndex);
    }

    // ---- DoD: unknown IncompatFlags refuse open with a typed error ----

    [Fact]
    public void Open_UnknownIncompatFlags_RefusesWithTypedError()
    {
        var sb = CreateSuperblock();
        sb.IncompatFlags = 0x0000_0004; // bit unknown to this implementation
        SeedFile(sb);

        var ex = Assert.Throws<IncompatibleFeatureFlagsException>(
            () => SuperblockSession.Open(OpenStream(), ownsStream: true));
        Assert.Equal(0x0000_0004U, ex.UnknownIncompatFlags);
    }

    [Fact]
    public void Open_UnknownIncompatFlags_DisposesOwnedStream()
    {
        var sb = CreateSuperblock();
        sb.IncompatFlags = 0x8000_0000;
        SeedFile(sb);

        var stream = OpenStream();
        Assert.Throws<IncompatibleFeatureFlagsException>(
            () => SuperblockSession.Open(stream, ownsStream: true));
        Assert.False(stream.CanRead); // disposed on refusal
    }

    [Fact]
    public void Open_UnknownIncompatFlags_MultipleBitsIncludingHighBit_ReportsAllOffendingBits()
    {
        var sb = CreateSuperblock();
        // Bits 0, 2, and the highest flag bit (31; flag fields are 4 bytes per
        // spec Section 3.1) — the typed error must report every offending bit.
        sb.IncompatFlags = 0x8000_0005;
        SeedFile(sb);

        var ex = Assert.Throws<IncompatibleFeatureFlagsException>(
            () => SuperblockSession.Open(OpenStream(), ownsStream: true));
        Assert.Equal(0x8000_0005U, ex.UnknownIncompatFlags);
        Assert.Contains("0x80000005", ex.Message); // offending bits surfaced to the operator
    }

    [Fact]
    public void Open_UnknownIncompatAndReadOnlyCompatFlags_IncompatRefusalWins()
    {
        var sb = CreateSuperblock();
        sb.IncompatFlags = 0x0000_0001;         // MUST refuse to open...
        sb.ReadOnlyCompatFlags = 0x0000_0001;   // ...read-only fallback must not apply
        sb.CompatFlags = 0x0000_0001;           // fully ignorable tier never matters
        SeedFile(sb);

        var ex = Assert.Throws<IncompatibleFeatureFlagsException>(
            () => SuperblockSession.Open(OpenStream(), ownsStream: true));
        Assert.Equal(0x0000_0001U, ex.UnknownIncompatFlags);
    }

    [Fact]
    public void Open_UnknownCompatFlags_ProceedsNormally()
    {
        var sb = CreateSuperblock();
        sb.CompatFlags = 0xFFFF_FFFF; // unknown Compat bits: reader proceeds normally
        SeedFile(sb);

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsSuccess, opened.Error);
        using var session = opened.Value;
        Assert.False(session.IsReadOnly);
        Assert.True(session.EnsureWritable().IsSuccess);
    }

    // ---- DoD: unknown ReadOnlyCompatFlags open succeeds but read-only is enforced ----

    [Fact]
    public void Open_UnknownReadOnlyCompatFlags_OpensReadOnly()
    {
        var sb = CreateSuperblock();
        sb.ReadOnlyCompatFlags = 0x0000_0010;
        SeedFile(sb);

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsSuccess, opened.Error); // open succeeds...
        using var session = opened.Value;
        Assert.True(session.IsReadOnly);             // ...but read-only
    }

    [Fact]
    public void ReadOnlySession_RefusesAllWrites_AndNeverTouchesDisk()
    {
        var sb = CreateSuperblock();
        sb.ReadOnlyCompatFlags = 0x0000_0010;
        SeedFile(sb); // seq 1, CleanShutdown = 1

        using (var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
        {
            var ensure = session.EnsureWritable();
            Assert.True(ensure.IsFailure);
            Assert.Contains("read-only", ensure.Error, StringComparison.OrdinalIgnoreCase);

            var update = session.UpdateSuperblock(CreateSuperblock());
            Assert.True(update.IsFailure);
            Assert.Contains("read-only", update.Error, StringComparison.OrdinalIgnoreCase);

            Assert.True(session.Close().IsSuccess); // close is a no-op, no write
        }

        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(1UL, onDisk.SuperblockSequence); // no superblock rewrite happened
        Assert.Equal(1, onDisk.CleanShutdown);        // still marked clean
    }

    [Fact]
    public void Open_UnknownReadOnlyCompatFlags_HighBit_OpensReadOnly()
    {
        var sb = CreateSuperblock();
        sb.ReadOnlyCompatFlags = 0x8000_0000; // highest flag bit (31; 4-byte field)
        SeedFile(sb);

        var opened = SuperblockSession.Open(OpenStream(), ownsStream: true);
        Assert.True(opened.IsSuccess, opened.Error);
        using var session = opened.Value;
        Assert.True(session.IsReadOnly);
    }

    [Fact]
    public void ReadOnlySession_WriteAttempts_LeaveFileByteIdentical()
    {
        var sb = CreateSuperblock();
        sb.ReadOnlyCompatFlags = 0xFFFF_FFFF; // every bit unknown
        SeedFile(sb);
        var before = File.ReadAllBytes(_path);

        using (var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
        {
            Assert.True(session.IsReadOnly);
            Assert.True(session.EnsureWritable().IsFailure);       // would clear CleanShutdown
            Assert.True(session.EnsureWritable().IsFailure);       // repeated attempts
            Assert.True(session.UpdateSuperblock(CreateSuperblock()).IsFailure);
            Assert.True(session.Close().IsSuccess);                // graceful close is a no-op
        }

        Assert.Equal(before, File.ReadAllBytes(_path)); // MUST NOT write: byte-identical
    }

    // ---- DoD: CleanShutdown = 0 on first write after open, = 1 on graceful close ----

    [Fact]
    public void EnsureWritable_FirstWriteAfterOpen_PersistsCleanShutdownZero()
    {
        SeedFile(CreateSuperblock()); // seq 1, CleanShutdown = 1

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        var first = session.EnsureWritable();
        Assert.True(first.IsSuccess, first.Error);

        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(0, onDisk.CleanShutdown);        // durably cleared before content writes
        Assert.Equal(2UL, onDisk.SuperblockSequence); // one superblock rewrite
    }

    [Fact]
    public void EnsureWritable_SecondCall_DoesNotRewriteSuperblock()
    {
        SeedFile(CreateSuperblock());

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.True(session.EnsureWritable().IsSuccess);
        Assert.True(session.EnsureWritable().IsSuccess);
        Assert.True(session.EnsureWritable().IsSuccess);

        Assert.Equal(2UL, ReadCurrentFromDisk().SuperblockSequence); // only the first call wrote
    }

    [Fact]
    public void Close_AfterWriting_PersistsCleanShutdownOne()
    {
        SeedFile(CreateSuperblock());

        using (var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
        {
            Assert.True(session.EnsureWritable().IsSuccess); // seq 2, CleanShutdown = 0
            var closed = session.Close();
            Assert.True(closed.IsSuccess, closed.Error);
            Assert.True(session.IsClosed);
        }

        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(1, onDisk.CleanShutdown);
        Assert.Equal(3UL, onDisk.SuperblockSequence);

        // A subsequent open sees the clean shutdown and can skip recovery scanning.
        using var reopened = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.True(reopened.WasCleanShutdown);
    }

    [Fact]
    public void Close_WithoutAnyWrite_DoesNotRewriteSuperblock()
    {
        SeedFile(CreateSuperblock()); // seq 1

        using (var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
            Assert.True(session.Close().IsSuccess);

        Assert.Equal(1UL, ReadCurrentFromDisk().SuperblockSequence); // untouched
    }

    [Fact]
    public void UpdateSuperblock_WhileSessionOpen_ForcesCleanShutdownZero()
    {
        SeedFile(CreateSuperblock());

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        var update = CreateSuperblock();
        update.CleanShutdown = 1; // caller value must be overridden while the session is open
        update.LastCheckpointOffset = 8192;
        var written = session.UpdateSuperblock(update);
        Assert.True(written.IsSuccess, written.Error);

        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(0, onDisk.CleanShutdown);
        Assert.Equal(8192, onDisk.LastCheckpointOffset);
    }

    [Fact]
    public void AbandonedSession_LeavesCleanShutdownZero_ForCrashRecovery()
    {
        SeedFile(CreateSuperblock());

        using (var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
            Assert.True(session.EnsureWritable().IsSuccess); // disposed WITHOUT Close (simulated crash)

        Assert.Equal(0, ReadCurrentFromDisk().CleanShutdown);
        using var reopened = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.False(reopened.WasCleanShutdown); // next open must run recovery
    }

    [Fact]
    public void UpdateSuperblock_AsFirstWrite_CountsAsFirstWrite_AndCloseRestoresOne()
    {
        SeedFile(CreateSuperblock()); // seq 1, CleanShutdown = 1

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;

        // UpdateSuperblock is the session's FIRST write: it must persist
        // CleanShutdown = 0 in that same durable write (spec Section 3.3).
        Assert.True(session.UpdateSuperblock(CreateSuperblock()).IsSuccess);
        var afterUpdate = ReadCurrentFromDisk();
        Assert.Equal(0, afterUpdate.CleanShutdown);
        Assert.Equal(2UL, afterUpdate.SuperblockSequence); // exactly one rewrite, not Ensure+Update

        // The session is now dirty: EnsureWritable must be a no-op, not a second rewrite.
        Assert.True(session.EnsureWritable().IsSuccess);
        Assert.Equal(2UL, ReadCurrentFromDisk().SuperblockSequence);

        // Graceful close after an UpdateSuperblock-only session still restores 1.
        Assert.True(session.Close().IsSuccess);
        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(1, onDisk.CleanShutdown);
        Assert.Equal(3UL, onDisk.SuperblockSequence);
    }

    [Fact]
    public void NeverWritingSession_DisposedWithoutClose_LeavesFileByteIdentical()
    {
        SeedFile(CreateSuperblock()); // seq 1, CleanShutdown = 1
        var before = File.ReadAllBytes(_path);

        // Open, read state, then dispose WITHOUT Close and WITHOUT any write:
        // the flag must stay untouched — 0 is written on first WRITE, not on open.
        using (var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
        {
            Assert.True(session.WasCleanShutdown);
            Assert.Equal(7U, session.Current.ShardIndex);
        }

        Assert.Equal(before, File.ReadAllBytes(_path)); // byte-identical: never wrote
        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(1, onDisk.CleanShutdown);
        Assert.Equal(1UL, onDisk.SuperblockSequence);

        // The next open still sees a clean shutdown — no spurious recovery.
        using var reopened = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.True(reopened.WasCleanShutdown);
    }

    [Fact]
    public void CrashedSession_NextSessionWriteAndClose_RestoresCleanShutdownOne()
    {
        SeedFile(CreateSuperblock()); // seq 1, CleanShutdown = 1

        // Session 1 writes, then "crashes" (disposed without Close): 0 stays on disk.
        using (var crashed = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
            Assert.True(crashed.EnsureWritable().IsSuccess); // seq 2, CleanShutdown = 0
        Assert.Equal(0, ReadCurrentFromDisk().CleanShutdown);

        // Session 2 detects the unclean shutdown, does its own write cycle, and
        // closes gracefully — the full spec Section 3.3 lifecycle back to 1.
        using (var recovery = SuperblockSession.Open(OpenStream(), ownsStream: true).Value)
        {
            Assert.False(recovery.WasCleanShutdown);          // recovery required
            Assert.True(recovery.EnsureWritable().IsSuccess); // seq 3, CleanShutdown = 0
            Assert.True(recovery.Close().IsSuccess);          // seq 4, CleanShutdown = 1
        }

        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(1, onDisk.CleanShutdown);
        Assert.Equal(4UL, onDisk.SuperblockSequence);
        using var reopened = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.True(reopened.WasCleanShutdown); // clean again after graceful close
    }

    [Fact]
    public void Writes_AfterClose_AreRefused()
    {
        SeedFile(CreateSuperblock());

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.True(session.EnsureWritable().IsSuccess);
        Assert.True(session.Close().IsSuccess);

        Assert.True(session.EnsureWritable().IsFailure);
        Assert.True(session.UpdateSuperblock(CreateSuperblock()).IsFailure);
        Assert.True(session.Close().IsSuccess); // idempotent
    }

    [Fact]
    public void Create_NewFile_WritesCleanShutdownZero_AndCloseSetsOne()
    {
        using (var created = SuperblockSession.Create(OpenStream(), CreateSuperblock(), ownsStream: true).Value)
        {
            Assert.Equal(0, ReadCurrentFromDisk().CleanShutdown); // active session on a new file
            Assert.True(created.Close().IsSuccess);
        }

        var onDisk = ReadCurrentFromDisk();
        Assert.Equal(1, onDisk.CleanShutdown);
        Assert.Equal(2UL, onDisk.SuperblockSequence);
    }

    // ---- DoD: MaxPayloadLength exposed to callers (block reader consumes it) ----

    [Fact]
    public void MaxPayloadLength_ExposesLoadedValue()
    {
        var sb = CreateSuperblock();
        sb.MaxPayloadLength = 123_456_789;
        SeedFile(sb);

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.Equal(123_456_789, session.MaxPayloadLength);
    }

    [Fact]
    public void MaxPayloadLength_DefaultsTo256MB()
    {
        SeedFile(CreateSuperblock());

        using var session = SuperblockSession.Open(OpenStream(), ownsStream: true).Value;
        Assert.Equal(Superblock.DefaultMaxPayloadLength, session.MaxPayloadLength);
        Assert.Equal(268_435_456, session.MaxPayloadLength);
    }
}
