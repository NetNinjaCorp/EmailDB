namespace EmailDB.Format.V3;

/// <summary>
/// Durability wrapper over a <see cref="FileStream"/> enforcing the v3 fsync
/// discipline (EmailDB_FileFormat_Spec.md Section 10.3) for every writer-side
/// component (<see cref="BlockManager"/> today; superblock/lock code as they
/// adopt it).
///
/// fsync means flush-to-disk: the only flush this wrapper ever issues is
/// <c>FileStream.Flush(flushToDisk: true)</c>. No bare stream flush is exposed,
/// so a buffer flush can never masquerade as durability.
///
/// fsync failure is fatal (never retried): if fsync reports an error, the
/// durability of everything written since the last successful fsync is
/// unknowable and the OS may already have dropped the dirty pages (the
/// "fsyncgate" lesson). The first failed write or fsync poisons the handle:
/// every subsequent <see cref="WriteAt(long, byte[], int, int)"/> and
/// <see cref="FlushToDisk"/> is refused with a failed <see cref="Result"/>
/// without touching the stream, and fsync is NEVER retried.
///
/// Recovery on reopen is forced by construction: a poisoned handle refuses all
/// further writes, so the clean-close superblock update that would set
/// <see cref="Superblock.CleanShutdown"/> = 1 can never be written. On-disk
/// CleanShutdown stays 0, and the next open takes the crash-recovery path
/// (spec Section 10.2 step 4: scan forward from the last valid Checkpoint and
/// replay WAL blocks), trusting only blocks that verify — the failure leaves
/// no illusion of durability.
///
/// Reads never poison and are never refused: they cannot affect durability,
/// and read errors surface to the caller per read (spec Section 13 corruption
/// handling lives in the callers).
///
/// Thread safety: <see cref="WriteAt(long, byte[], int, int)"/> and
/// <see cref="FlushToDisk"/> are individually atomic (internal lock guards the
/// poison flag and the write position). The stream position is shared state,
/// so compound seek-then-read sequences require external synchronization —
/// exactly what <see cref="BlockManager"/>'s stream lock provides.
/// </summary>
public sealed class DurableStream : IDisposable
{
    /// <summary>
    /// Refusal message for a poisoned handle (spec Section 10.3): durability is
    /// unknowable, fsync is never retried, and reopen runs crash recovery
    /// because the clean-shutdown mark can no longer be written.
    /// </summary>
    private const string PoisonedError =
        "Handle is poisoned after a failed write/fsync: durability of prior writes is " +
        "unknowable and fsync is never retried (spec Section 10.3). Refusing further " +
        "writes; close the file — CleanShutdown remains 0, so reopen runs crash recovery.";

    private readonly FileStream _stream;
    private readonly bool _ownsStream;

    /// <summary>Guards the poison flag, fsync counter, and write seek+write pairs.</summary>
    private readonly object _writeLock = new();
    private bool _disposed;
    private volatile bool _poisoned;

    /// <summary>
    /// Wraps an open, readable, writable, seekable stream of an EmailDB file.
    /// </summary>
    /// <param name="stream">Stream over the EmailDB file, positioned anywhere.</param>
    /// <param name="ownsStream">When true, disposing the wrapper disposes the stream.</param>
    public DurableStream(FileStream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException(
                "Durable stream must be readable, writable, and seekable.", nameof(stream));
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>
    /// True after a failed write or fsync: durability is unknowable, so the
    /// handle refuses all further writes and flushes (spec Section 10.3).
    /// </summary>
    public bool IsPoisoned => _poisoned;

    /// <summary>Number of successful flush-to-disk (fsync) operations.</summary>
    public long FlushToDiskCount { get; private set; }

    /// <summary>Current length of the underlying file in bytes.</summary>
    public long Length => _stream.Length;

    /// <summary>
    /// Writes <paramref name="count"/> bytes at the given file offset. The
    /// write is buffered; call <see cref="FlushToDisk"/> at commit points. A
    /// failed write poisons the handle: a torn write may leave garbage on
    /// disk, and writing after it would bury the damage (spec Sections 10.3, 13).
    /// </summary>
    /// <param name="offset">Absolute file offset to write at.</param>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="bufferOffset">Offset of the first byte to write within <paramref name="buffer"/>.</param>
    /// <param name="count">Number of bytes to write.</param>
    public Result WriteAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);

        lock (_writeLock)
        {
            if (_poisoned)
                return Result.Failure(PoisonedError);

            try
            {
                _stream.Seek(offset, SeekOrigin.Begin);
                _stream.Write(buffer, bufferOffset, count);
            }
            catch (IOException ex)
            {
                _poisoned = true;
                return Result.Failure(
                    $"Write of {count} bytes at offset {offset} failed: {ex.Message}. {PoisonedError}");
            }

            return Result.Success();
        }
    }

    /// <summary>Writes all of <paramref name="buffer"/> at the given file offset.</summary>
    /// <param name="offset">Absolute file offset to write at.</param>
    /// <param name="buffer">Bytes to write.</param>
    public Result WriteAt(long offset, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAt(offset, buffer, 0, buffer.Length);
    }

    /// <summary>
    /// Flushes all buffered writes to disk — fsync, i.e.
    /// <c>Flush(flushToDisk: true)</c>, never a bare stream flush. fsync
    /// failure is fatal (spec Section 10.3): the handle is poisoned, further
    /// writes and flushes are refused, fsync is never retried, and the caller
    /// must close the file and rely on crash recovery on reopen.
    /// </summary>
    public Result FlushToDisk()
    {
        ThrowIfDisposed();

        lock (_writeLock)
        {
            if (_poisoned)
                return Result.Failure(PoisonedError);

            try
            {
                // The only flush this type ever issues: flush-to-disk (fsync).
                _stream.Flush(flushToDisk: true);
            }
            catch (IOException ex)
            {
                _poisoned = true;
                return Result.Failure($"fsync failed: {ex.Message}. {PoisonedError}");
            }

            FlushToDiskCount++;
            return Result.Success();
        }
    }

    /// <summary>Positions the stream at the given absolute offset (read path).</summary>
    /// <param name="offset">Absolute file offset.</param>
    public void Seek(long offset) => _stream.Seek(offset, SeekOrigin.Begin);

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes from the current position.</summary>
    public void ReadExactly(Span<byte> buffer) => _stream.ReadExactly(buffer);

    /// <summary>Reads exactly <paramref name="count"/> bytes from the current position.</summary>
    public void ReadExactly(byte[] buffer, int offset, int count) =>
        _stream.ReadExactly(buffer, offset, count);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsStream)
            _stream.Dispose();
    }
}
