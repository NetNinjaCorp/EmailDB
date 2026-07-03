namespace EmailDB.Format.V3;

/// <summary>
/// Open-time policy layer over <see cref="SuperblockManager"/> implementing the
/// v3 open path (EmailDB_FileFormat_Spec.md Sections 3.3 and 10.2):
/// <list type="bullet">
/// <item>Open validates both slots (magic + BLAKE3-128 checksum, via
/// <see cref="SuperblockManager.Load"/>) and adopts the higher valid sequence.</item>
/// <item>Unknown <see cref="Superblock.IncompatFlags"/> bits refuse the open with a
/// typed <see cref="IncompatibleFeatureFlagsException"/>.</item>
/// <item>Unknown <see cref="Superblock.ReadOnlyCompatFlags"/> bits open the session
/// read-only: every write path fails and nothing is persisted.</item>
/// <item><c>CleanShutdown = 0</c> is durably written on the first write of the session
/// (<see cref="EnsureWritable"/> or <see cref="UpdateSuperblock"/>) and
/// <c>CleanShutdown = 1</c> on graceful <see cref="Close"/>.</item>
/// <item><see cref="MaxPayloadLength"/> exposes the loaded sanity bound for the
/// block reader.</item>
/// </list>
/// <see cref="Dispose"/> does NOT perform a graceful close — an abandoned session
/// leaves <c>CleanShutdown = 0</c> on disk (if it wrote anything), so the next open
/// runs crash recovery, exactly as after a crash. Call <see cref="Close"/> for a
/// clean shutdown.
/// </summary>
public sealed class SuperblockSession : IDisposable
{
    private readonly SuperblockManager _manager;

    /// <summary>True once this session has persisted CleanShutdown = 0.</summary>
    private bool _dirty;

    private bool _closed;
    private bool _disposed;

    private SuperblockSession(SuperblockManager manager, bool isReadOnly, bool wasCleanShutdown)
    {
        _manager = manager;
        IsReadOnly = isReadOnly;
        WasCleanShutdown = wasCleanShutdown;
    }

    /// <summary>The current in-memory superblock (last loaded or written).</summary>
    public Superblock Current => _manager.Current!;

    /// <summary>
    /// True when unknown <see cref="Superblock.ReadOnlyCompatFlags"/> bits forced this
    /// session read-only. All write paths fail and the file is never modified.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// CleanShutdown value found at open: true = last session closed cleanly (the
    /// LastCheckpoint hint is current); false = crash recovery scanning is required
    /// (spec Section 10.2). True for freshly created files.
    /// </summary>
    public bool WasCleanShutdown { get; }

    /// <summary>
    /// Sanity bound for block PayloadLength, from the loaded superblock
    /// (spec Section 3.1). The block reader must validate lengths against this
    /// before allocating.
    /// </summary>
    public long MaxPayloadLength => Current.MaxPayloadLength;

    /// <summary>True once <see cref="Close"/> completed; the session no longer accepts writes.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Opens an existing file: validates both superblock slots (magic + checksum)
    /// and adopts the higher valid sequence, then enforces feature-flag policy.
    /// </summary>
    /// <exception cref="IncompatibleFeatureFlagsException">
    /// The superblock carries unknown IncompatFlags bits; the file must not be opened.
    /// </exception>
    /// <returns>
    /// Failure (no exception) when neither slot holds a valid superblock; the caller
    /// may fall back to a full scan per spec Section 13.
    /// </returns>
    public static Result<SuperblockSession> Open(FileStream stream, bool ownsStream = false)
    {
        var manager = new SuperblockManager(stream, ownsStream);
        Result<Superblock> loaded;
        try
        {
            loaded = manager.Load();
        }
        catch
        {
            manager.Dispose();
            throw;
        }

        if (loaded.IsFailure)
        {
            manager.Dispose();
            return Result<SuperblockSession>.Failure($"Cannot open EmailDB file: {loaded.Error}");
        }

        var unknownIncompat = SuperblockFeatureFlags.UnknownIncompatBits(loaded.Value);
        if (unknownIncompat != 0)
        {
            manager.Dispose();
            throw new IncompatibleFeatureFlagsException(unknownIncompat);
        }

        var isReadOnly = SuperblockFeatureFlags.UnknownReadOnlyCompatBits(loaded.Value) != 0;
        return Result<SuperblockSession>.Success(
            new SuperblockSession(manager, isReadOnly, wasCleanShutdown: loaded.Value.CleanShutdown == 1));
    }

    /// <summary>
    /// Creates the superblock in a new (empty) file and returns an open, writable
    /// session. The initial superblock is durably written with CleanShutdown = 0
    /// (the session is active); a graceful <see cref="Close"/> then sets it to 1.
    /// </summary>
    public static Result<SuperblockSession> Create(FileStream stream, Superblock superblock, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(superblock);
        var manager = new SuperblockManager(stream, ownsStream);

        var initial = superblock.Clone();
        initial.CleanShutdown = 0;

        Result<Superblock> written;
        try
        {
            written = manager.Write(initial);
        }
        catch
        {
            manager.Dispose();
            throw;
        }

        if (written.IsFailure)
        {
            manager.Dispose();
            return Result<SuperblockSession>.Failure($"Cannot create EmailDB superblock: {written.Error}");
        }

        return Result<SuperblockSession>.Success(
            new SuperblockSession(manager, isReadOnly: false, wasCleanShutdown: true) { _dirty = true });
    }

    /// <summary>
    /// Must be called (directly, or implicitly via <see cref="UpdateSuperblock"/>)
    /// before the first content write of the session. On the first call it durably
    /// rewrites the superblock with CleanShutdown = 0 (spec Section 3.3), so a crash
    /// from that point on is detected at the next open. Subsequent calls are no-ops.
    /// Fails without touching the file when the session is read-only or closed.
    /// </summary>
    public Result EnsureWritable()
    {
        var writable = CheckWritable();
        if (writable.IsFailure)
            return writable;
        if (_dirty)
            return Result.Success();

        var updated = Current.Clone();
        updated.CleanShutdown = 0;
        var written = _manager.Write(updated);
        if (written.IsFailure)
            return Result.Failure($"Failed to clear CleanShutdown on first write: {written.Error}");

        _dirty = true;
        return Result.Success();
    }

    /// <summary>
    /// Durably writes an updated superblock (checkpoint hint, key rotation, periodic
    /// rewrite, ...). While the session is open the persisted CleanShutdown is forced
    /// to 0 regardless of the value on <paramref name="superblock"/> — only a graceful
    /// <see cref="Close"/> writes CleanShutdown = 1. Counts as the session's first
    /// write if none happened yet.
    /// </summary>
    public Result<Superblock> UpdateSuperblock(Superblock superblock)
    {
        ArgumentNullException.ThrowIfNull(superblock);
        var writable = CheckWritable();
        if (writable.IsFailure)
            return Result<Superblock>.Failure(writable.Error);

        var updated = superblock.Clone();
        updated.CleanShutdown = 0;
        var written = _manager.Write(updated);
        if (written.IsSuccess)
            _dirty = true;
        return written;
    }

    /// <summary>
    /// Graceful close (spec Section 3.3): if the session wrote anything, durably
    /// rewrites the superblock with CleanShutdown = 1 so the next open can trust the
    /// LastCheckpoint hint and skip recovery scanning. A session that never wrote
    /// (or is read-only) closes without touching the file. Idempotent; on failure
    /// the session stays open and Close may be retried.
    /// </summary>
    public Result Close()
    {
        ThrowIfDisposed();
        if (_closed)
            return Result.Success();

        if (IsReadOnly || !_dirty)
        {
            _closed = true;
            return Result.Success();
        }

        var final = Current.Clone();
        final.CleanShutdown = 1;
        var written = _manager.Write(final);
        if (written.IsFailure)
            return Result.Failure($"Graceful close failed to set CleanShutdown: {written.Error}");

        _closed = true;
        return Result.Success();
    }

    private Result CheckWritable()
    {
        ThrowIfDisposed();
        if (_closed)
            return Result.Failure("Superblock session is closed.");
        if (IsReadOnly)
            return Result.Failure(
                "File is read-only: superblock ReadOnlyCompatFlags contains unknown bits " +
                $"0x{SuperblockFeatureFlags.UnknownReadOnlyCompatBits(Current):X8}; this implementation MUST NOT write.");
        return Result.Success();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Releases the underlying manager (and stream, when owned). Does NOT write
    /// CleanShutdown = 1 — call <see cref="Close"/> first for a graceful shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _manager.Dispose();
    }
}
