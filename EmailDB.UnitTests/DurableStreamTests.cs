using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Fault-injectable FileStream for fsync-discipline tests
/// (EmailDB_FileFormat_Spec.md Section 10.3). Counts flush-to-disk (fsync)
/// calls separately from non-durable buffer flushes, and can fail writes or a
/// bounded number of fsyncs on demand.
/// </summary>
internal sealed class FaultInjectingFileStream : FileStream
{
    public FaultInjectingFileStream(string path)
        : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
    }

    /// <summary>Calls to <c>Flush(flushToDisk: true)</c> — real fsync attempts.</summary>
    public int FsyncCalls { get; private set; }

    /// <summary>Calls to <c>Flush()</c> / <c>Flush(false)</c> — non-durable buffer flushes.</summary>
    public int NonDurableFlushCalls { get; private set; }

    /// <summary>Number of upcoming fsync attempts to fail with an IOException.</summary>
    public int FailNextFsyncs { get; set; }

    /// <summary>When true, every write throws an IOException.</summary>
    public bool FailWrites { get; set; }

    public override void Flush()
    {
        NonDurableFlushCalls++;
        base.Flush(); // may dispatch back into Flush(false); double-counting a bare flush is fine
    }

    public override void Flush(bool flushToDisk)
    {
        if (flushToDisk)
        {
            FsyncCalls++;
            if (FailNextFsyncs > 0)
            {
                FailNextFsyncs--;
                throw new IOException("injected fsync failure");
            }
        }
        else
        {
            NonDurableFlushCalls++;
        }

        base.Flush(flushToDisk);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (FailWrites)
            throw new IOException("injected write failure");
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (FailWrites)
            throw new IOException("injected write failure");
        base.Write(buffer);
    }
}

/// <summary>
/// Tests for <see cref="DurableStream"/>, the fsync wrapper with fatal-poison
/// semantics (spec Section 10.3): flush-to-disk means fsync (Flush(true),
/// never a bare stream flush), the first failed write or fsync poisons the
/// handle, a poisoned handle refuses writes and flushes with a failed Result,
/// and fsync is never retried.
/// </summary>
public class DurableStreamTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-durablestream-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FaultInjectingFileStream OpenFaultStream() => new(_path);

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    // ---- fsync means flush-to-disk, not stream flush ----

    [Fact]
    public void FlushToDisk_IssuesFsync_NeverBareStreamFlush()
    {
        using var inner = OpenFaultStream();
        using var durable = new DurableStream(inner);

        Assert.True(durable.WriteAt(0, SamplePayload(64)).IsSuccess);
        var flush = durable.FlushToDisk();

        Assert.True(flush.IsSuccess, flush.Error);
        Assert.Equal(1, inner.FsyncCalls);          // Flush(flushToDisk: true) was used...
        Assert.Equal(0, inner.NonDurableFlushCalls); // ...and a bare Flush()/Flush(false) never was
        Assert.Equal(1, durable.FlushToDiskCount);
        Assert.False(durable.IsPoisoned);
    }

    [Fact]
    public void DurableStream_ExposesNoBareFlushMethod()
    {
        // The wrapper's contract is that a buffer flush can never masquerade as
        // durability: the ONLY flush on the public surface is FlushToDisk().
        // If someone adds a Flush()/Flush(bool) passthrough, this fails.
        var flushMethods = typeof(DurableStream)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name == "Flush")
            .ToList();

        Assert.Empty(flushMethods);
        Assert.NotNull(typeof(DurableStream).GetMethod("FlushToDisk", Type.EmptyTypes));
    }

    [Fact]
    public void FullLifecycle_IncludingDispose_NeverIssuesBareStreamFlush()
    {
        var inner = OpenFaultStream();
        using (var durable = new DurableStream(inner, ownsStream: true))
        {
            // Exercise every write-side code path: writes, fsyncs, reads.
            Assert.True(durable.WriteAt(0, SamplePayload(64)).IsSuccess);
            Assert.True(durable.FlushToDisk().IsSuccess);
            Assert.True(durable.WriteAt(64, SamplePayload(32, seed: 5)).IsSuccess);
            Assert.True(durable.FlushToDisk().IsSuccess);

            var readBack = new byte[96];
            durable.Seek(0);
            durable.ReadExactly(readBack);
        } // Dispose of the wrapper disposes the FileStream too.

        // Every flush that reached the OS was a flush-to-disk (fsync); no code
        // path — not even Dispose — issued a bare Flush()/Flush(false).
        Assert.Equal(2, inner.FsyncCalls);
        Assert.Equal(0, inner.NonDurableFlushCalls);
    }

    [Fact]
    public void WriteAtAndFlushToDisk_DataIsReadableFromDisk()
    {
        var payload = SamplePayload(128, seed: 3);
        using (var inner = OpenFaultStream())
        using (var durable = new DurableStream(inner))
        {
            Assert.True(durable.WriteAt(0, payload).IsSuccess);
            Assert.True(durable.FlushToDisk().IsSuccess);

            var readBack = new byte[payload.Length];
            durable.Seek(0);
            durable.ReadExactly(readBack);
            Assert.Equal(payload, readBack);
        }

        Assert.Equal(payload, File.ReadAllBytes(_path));
    }

    // ---- first fsync failure poisons ----

    [Fact]
    public void FirstFsyncFailure_PoisonsHandle_AndReportsFailedResult()
    {
        using var inner = OpenFaultStream();
        using var durable = new DurableStream(inner);
        Assert.True(durable.WriteAt(0, SamplePayload(32)).IsSuccess);

        inner.FailNextFsyncs = 1;
        var flush = durable.FlushToDisk();

        Assert.True(flush.IsFailure);
        Assert.Contains("fsync failed", flush.Error);
        Assert.Contains("injected fsync failure", flush.Error);
        Assert.Contains("crash recovery", flush.Error);
        Assert.True(durable.IsPoisoned);
        Assert.Equal(0, durable.FlushToDiskCount); // the failed fsync is not a successful flush
    }

    // ---- poisoned handle refuses writes and flushes; fsync is never retried ----

    [Fact]
    public void PoisonedHandle_RefusesFlush_AndNeverRetriesFsync()
    {
        using var inner = OpenFaultStream();
        using var durable = new DurableStream(inner);
        Assert.True(durable.WriteAt(0, SamplePayload(32)).IsSuccess);

        inner.FailNextFsyncs = 1;
        Assert.True(durable.FlushToDisk().IsFailure);
        Assert.Equal(1, inner.FsyncCalls);

        // The injected fault is gone: a retry WOULD succeed — which is exactly
        // what the spec forbids. The poisoned handle must refuse without ever
        // reaching the OS again.
        Assert.Equal(0, inner.FailNextFsyncs);
        for (int i = 0; i < 3; i++)
        {
            var again = durable.FlushToDisk();
            Assert.True(again.IsFailure);
            Assert.Contains("poisoned", again.Error);
        }

        Assert.Equal(1, inner.FsyncCalls); // still exactly the one failed attempt
        Assert.True(durable.IsPoisoned);
    }

    [Fact]
    public void PoisonedHandle_RefusesWrites_WithoutTouchingTheFile()
    {
        using var inner = OpenFaultStream();
        using var durable = new DurableStream(inner);
        Assert.True(durable.WriteAt(0, SamplePayload(32)).IsSuccess);
        Assert.True(durable.FlushToDisk().IsSuccess);
        var lengthBefore = durable.Length;

        inner.FailNextFsyncs = 1;
        Assert.True(durable.FlushToDisk().IsFailure);

        var write = durable.WriteAt(lengthBefore, SamplePayload(64));
        Assert.True(write.IsFailure);
        Assert.Contains("poisoned", write.Error);
        Assert.Equal(lengthBefore, durable.Length); // nothing was appended
    }

    [Fact]
    public void WriteFailure_Poisons_AndSubsequentFlushIsRefusedWithoutFsync()
    {
        using var inner = OpenFaultStream();
        using var durable = new DurableStream(inner);

        inner.FailWrites = true;
        var write = durable.WriteAt(0, SamplePayload(16));

        Assert.True(write.IsFailure);
        Assert.Contains("injected write failure", write.Error);
        Assert.True(durable.IsPoisoned);

        // A flush after a failed write must be refused BEFORE reaching fsync:
        // it could otherwise succeed and fake durability for a torn write.
        inner.FailWrites = false;
        var flush = durable.FlushToDisk();
        Assert.True(flush.IsFailure);
        Assert.Contains("poisoned", flush.Error);
        Assert.Equal(0, inner.FsyncCalls);
    }

    [Fact]
    public void PoisonedHandle_StillAllowsReads()
    {
        using var inner = OpenFaultStream();
        using var durable = new DurableStream(inner);
        var payload = SamplePayload(48, seed: 7);
        Assert.True(durable.WriteAt(0, payload).IsSuccess);
        Assert.True(durable.FlushToDisk().IsSuccess);

        inner.FailNextFsyncs = 1;
        Assert.True(durable.FlushToDisk().IsFailure);
        Assert.True(durable.IsPoisoned);

        // Reads cannot affect durability: recovery-oriented readers may still
        // inspect the file through the poisoned handle.
        var readBack = new byte[payload.Length];
        durable.Seek(0);
        durable.ReadExactly(readBack);
        Assert.Equal(payload, readBack);
    }
}

/// <summary>
/// End-to-end acceptance tests for the US-EMDB-65 criterion "fsync failure
/// poisons the handle and forces recovery on reopen": a writer whose
/// <see cref="DurableStream"/> was poisoned by an fsync failure can never write
/// the CleanShutdown = 1 superblock mark (the write is refused without reaching
/// the OS), so on-disk <see cref="Superblock.CleanShutdown"/> stays 0 and the
/// next <see cref="SuperblockSession.Open"/> reports an unclean shutdown —
/// forcing the crash-recovery path (spec Sections 3.3, 10.2, 10.3).
/// </summary>
public class DurableStreamPoisonForcesRecoveryOnReopenTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-poison-reopen-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static Superblock CreateSuperblock() => new()
    {
        FileId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
        ShardIndex = 3,
        CreatedTimestamp = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc).Ticks,
        CleanShutdown = 1,
    };

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    /// <summary>
    /// Serialized superblock slot carrying CleanShutdown = 1 — the clean-close
    /// mark a graceful shutdown would write (next sequence, per the dual-slot
    /// protocol).
    /// </summary>
    private static byte[] CleanShutdownMark(SuperblockSession session)
    {
        var final = session.Current.Clone();
        final.CleanShutdown = 1;
        final.SuperblockSequence = session.Current.SuperblockSequence + 1;
        return SuperblockSerializer.Serialize(final);
    }

    [Fact]
    public void PoisonedWriter_CannotWriteCleanShutdownMark_ReopenForcesRecovery()
    {
        long fsyncCallsAtPoison;

        // ---- Writer session: create file, write content, then hit an fsync failure ----
        using (var inner = new FaultInjectingFileStream(_path))
        {
            var created = SuperblockSession.Create(inner, CreateSuperblock());
            Assert.True(created.IsSuccess, created.Error);
            using var session = created.Value; // CleanShutdown = 0 on disk: session active
            using var durable = new DurableStream(inner);

            // Healthy content write + fsync past the superblock region.
            Assert.True(durable.WriteAt(
                SuperblockManager.SuperblockRegionSize, SamplePayload(128)).IsSuccess);
            Assert.True(durable.FlushToDisk().IsSuccess);

            // fsync failure at the next commit point poisons the handle.
            inner.FailNextFsyncs = 1;
            Assert.True(durable.FlushToDisk().IsFailure);
            Assert.True(durable.IsPoisoned);
            fsyncCallsAtPoison = inner.FsyncCalls;

            // The clean-close superblock update (CleanShutdown = 1) can never be
            // written through the poisoned handle — refused for BOTH slots
            // without touching the stream, and no fsync could make it durable.
            var mark = CleanShutdownMark(session);
            foreach (var slotOffset in new[] { SuperblockManager.SlotAOffset, SuperblockManager.SlotBOffset })
            {
                var attempt = durable.WriteAt(slotOffset, mark);
                Assert.True(attempt.IsFailure);
                Assert.Contains("poisoned", attempt.Error);
            }
            Assert.True(durable.FlushToDisk().IsFailure);
            Assert.Equal(fsyncCallsAtPoison, inner.FsyncCalls); // fsync never retried

            // Writer closes the file WITHOUT a graceful Close (the only correct
            // response to poison): CleanShutdown = 0 stays on disk.
        }

        // ---- Reopen: the unclean shutdown is detected and recovery is forced ----
        using var reopenStream = new FileStream(
            _path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var reopened = SuperblockSession.Open(reopenStream, ownsStream: true);
        Assert.True(reopened.IsSuccess, reopened.Error);
        using var recovery = reopened.Value;

        Assert.Equal(0, recovery.Current.CleanShutdown); // clean-close mark never landed
        Assert.False(recovery.WasCleanShutdown);         // open MUST take the recovery path
    }

    [Fact]
    public void SameFlow_WithoutFsyncFailure_GracefulCloseReopensClean()
    {
        // Control for the poison test: the identical write flow WITHOUT the
        // injected fsync failure closes cleanly and reopens with
        // WasCleanShutdown = true — proving it is the poisoning alone that
        // forces recovery in the test above.
        using (var inner = new FaultInjectingFileStream(_path))
        {
            var created = SuperblockSession.Create(inner, CreateSuperblock());
            Assert.True(created.IsSuccess, created.Error);
            using var session = created.Value;
            using var durable = new DurableStream(inner);

            Assert.True(durable.WriteAt(
                SuperblockManager.SuperblockRegionSize, SamplePayload(128)).IsSuccess);
            Assert.True(durable.FlushToDisk().IsSuccess);
            Assert.True(durable.FlushToDisk().IsSuccess); // second commit point, no fault
            Assert.False(durable.IsPoisoned);

            var closed = session.Close(); // graceful close writes CleanShutdown = 1
            Assert.True(closed.IsSuccess, closed.Error);
        }

        using var reopenStream = new FileStream(
            _path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var reopened = SuperblockSession.Open(reopenStream, ownsStream: true);
        Assert.True(reopened.IsSuccess, reopened.Error);
        using var session2 = reopened.Value;

        Assert.Equal(1, session2.Current.CleanShutdown);
        Assert.True(session2.WasCleanShutdown); // no recovery needed
    }
}

/// <summary>
/// Tests that <see cref="BlockManager"/> observes the fsync discipline through
/// <see cref="DurableStream"/> (spec Section 10.3): Flush() is a real
/// flush-to-disk, an fsync failure poisons the manager, a poisoned manager
/// refuses appends and flushes, and fsync is never retried.
/// </summary>
public class BlockManagerFsyncTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-blockmanager-fsync-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private (BlockManager Manager, FaultInjectingFileStream Stream) CreateManager()
    {
        var stream = new FaultInjectingFileStream(_path);
        var manager = new BlockManager(stream, firstBlockOffset: 0, ownsStream: true);
        return (manager, stream);
    }

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    [Fact]
    public void Flush_UsesFlushToDiskTrue_NeverBareStreamFlush()
    {
        var (manager, stream) = CreateManager();
        using (manager)
        {
            Assert.True(manager.Append(
                BlockType.WAL, PayloadEncoding.Custom, SamplePayload(20)).IsSuccess);
            var flush = manager.Flush();

            Assert.True(flush.IsSuccess, flush.Error);
            Assert.Equal(1, stream.FsyncCalls);
            Assert.Equal(0, stream.NonDurableFlushCalls);
            Assert.Equal(1, manager.FlushToDiskCount);
        }
    }

    [Fact]
    public void FullLifecycle_IncludingDispose_EveryFlushIsFsync_NoBareFlushEver()
    {
        var (manager, stream) = CreateManager();
        using (manager)
        {
            // Multiple append/flush cycles: every manager Flush() must reach the
            // OS as Flush(flushToDisk: true), one fsync per flush, nothing else.
            for (int i = 0; i < 3; i++)
            {
                Assert.True(manager.Append(
                    BlockType.WAL, PayloadEncoding.Custom, SamplePayload(20, seed: i + 1)).IsSuccess);
                Assert.True(manager.Flush().IsSuccess);
            }

            Assert.Equal(3, stream.FsyncCalls);
            Assert.Equal(3, manager.FlushToDiskCount);
        } // Dispose the manager (owns the stream) — must not bare-flush either.

        Assert.Equal(3, stream.FsyncCalls);
        Assert.Equal(0, stream.NonDurableFlushCalls);
    }

    [Fact]
    public void FsyncFailure_PoisonsManager_AndFlushNeverRetriesFsync()
    {
        var (manager, stream) = CreateManager();
        using (manager)
        {
            Assert.True(manager.Append(
                BlockType.WAL, PayloadEncoding.Custom, SamplePayload(20)).IsSuccess);

            stream.FailNextFsyncs = 1;
            var flush = manager.Flush();
            Assert.True(flush.IsFailure);
            Assert.Contains("fsync failed", flush.Error);
            Assert.True(manager.IsPoisoned);
            Assert.Equal(1, stream.FsyncCalls);

            // The fault is cleared — a retry would succeed, and must not happen.
            var again = manager.Flush();
            Assert.True(again.IsFailure);
            Assert.Contains("poisoned", again.Error);
            Assert.Equal(1, stream.FsyncCalls);
            Assert.Equal(0, manager.FlushToDiskCount);
        }
    }

    [Fact]
    public void PoisonedManager_RefusesAppends()
    {
        var (manager, stream) = CreateManager();
        using (manager)
        {
            Assert.True(manager.Append(
                BlockType.WAL, PayloadEncoding.Custom, SamplePayload(20)).IsSuccess);
            stream.FailNextFsyncs = 1;
            Assert.True(manager.Flush().IsFailure);

            var append = manager.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(10));
            Assert.True(append.IsFailure);
            Assert.Contains("poisoned", append.Error);
        }
    }

    [Fact]
    public void AppendWriteFailure_PoisonsManager_AndRefusesFurtherWritesAndFlushes()
    {
        var (manager, stream) = CreateManager();
        using (manager)
        {
            stream.FailWrites = true;
            var append = manager.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(30));
            Assert.True(append.IsFailure);
            Assert.Contains("Block append failed", append.Error);
            Assert.True(manager.IsPoisoned);

            // Even with writes healthy again, the poisoned manager refuses:
            // an fsync after a torn append would fake durability for garbage.
            stream.FailWrites = false;
            Assert.True(manager.Append(
                BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(30)).IsFailure);
            Assert.True(manager.Flush().IsFailure);
            Assert.Equal(0, stream.FsyncCalls);
        }
    }
}
