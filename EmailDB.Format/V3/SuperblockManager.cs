namespace EmailDB.Format.V3;

/// <summary>
/// Implements the v3 dual-slot superblock write protocol
/// (EmailDB_FileFormat_Spec.md Section 3): two fixed 4096-byte slots at file
/// offsets 0 (slot A) and 4096 (slot B). Writes alternate between the slots,
/// each write increments <see cref="Superblock.SuperblockSequence"/> and is
/// flushed to disk (fsync) before returning, so a torn superblock write never
/// destroys the previous good superblock.
///
/// On <see cref="Load"/>, both slots are validated (magic + BLAKE3-128 checksum,
/// via <see cref="SuperblockSerializer.Deserialize"/>) and the slot with the
/// higher valid sequence wins. Because updates always target the slot that does
/// NOT hold the current superblock, an invalid (torn) slot is exactly the slot
/// rewritten — and thereby repaired — on the next update.
///
/// Open-time policy (feature-flag enforcement, CleanShutdown transitions) is
/// layered on top of this class and is out of scope here.
/// </summary>
public sealed class SuperblockManager : IDisposable
{
    /// <summary>File offset of superblock slot A.</summary>
    public const long SlotAOffset = 0;

    /// <summary>File offset of superblock slot B.</summary>
    public const long SlotBOffset = SuperblockSerializer.SlotSize;

    /// <summary>Total on-disk size of the dual-slot superblock region.</summary>
    public const long SuperblockRegionSize = 2L * SuperblockSerializer.SlotSize;

    private readonly FileStream _stream;
    private readonly bool _ownsStream;
    private bool _disposed;

    /// <summary>Slot (0 = A, 1 = B) holding the current superblock; -1 = none yet.</summary>
    private int _currentSlot = -1;

    /// <summary>Sequence of the last known good superblock; next write uses this + 1.</summary>
    private ulong _lastSequence;

    private Superblock? _current;

    /// <summary>
    /// Creates a manager over an open, readable and writable file stream.
    /// Call <see cref="Load"/> to adopt the existing on-disk state of a
    /// non-empty file before writing updates.
    /// </summary>
    /// <param name="stream">Stream over the EmailDB file, positioned anywhere.</param>
    /// <param name="ownsStream">When true, disposing the manager disposes the stream.</param>
    public SuperblockManager(FileStream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
            throw new ArgumentException("Superblock stream must be readable, writable, and seekable.", nameof(stream));
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>The superblock most recently loaded or written; null before either.</summary>
    public Superblock? Current => _current;

    /// <summary>Slot index (0 = A, 1 = B) holding <see cref="Current"/>; null before any load/write.</summary>
    public int? CurrentSlot => _currentSlot >= 0 ? _currentSlot : null;

    /// <summary>Number of successful flush-to-disk (fsync) operations performed by this manager.</summary>
    public long FlushToDiskCount { get; private set; }

    /// <summary>
    /// Reads and validates both slots and adopts the one with the higher valid
    /// sequence as <see cref="Current"/>. Fails when neither slot holds a valid
    /// superblock (e.g. a brand-new empty file, or both slots corrupt).
    /// </summary>
    public Result<Superblock> Load()
    {
        ThrowIfDisposed();

        var slotA = ReadSlot(SlotAOffset);
        var slotB = ReadSlot(SlotBOffset);

        if (slotA.IsFailure && slotB.IsFailure)
            return Result<Superblock>.Failure(
                $"No valid superblock found. Slot A: {slotA.Error} Slot B: {slotB.Error}");

        int winner;
        if (slotA.IsSuccess && slotB.IsSuccess)
            winner = slotB.Value.SuperblockSequence > slotA.Value.SuperblockSequence ? 1 : 0;
        else
            winner = slotA.IsSuccess ? 0 : 1;

        _current = winner == 0 ? slotA.Value : slotB.Value;
        _currentSlot = winner;
        _lastSequence = _current.SuperblockSequence;
        return Result<Superblock>.Success(_current);
    }

    /// <summary>
    /// Durably writes an updated superblock: assigns the next monotonic
    /// <see cref="Superblock.SuperblockSequence"/>, serializes into the slot
    /// that does not hold the current superblock (slot A when there is none
    /// yet), writes it at that slot's fixed offset, and flushes to disk before
    /// returning. Because the write targets the non-current slot, a slot found
    /// invalid (torn) at load time is repaired by the next update, and the
    /// previous good superblock is never overwritten.
    /// </summary>
    /// <param name="superblock">
    /// Superblock content to persist. Its <see cref="Superblock.SuperblockSequence"/>
    /// is overwritten with the assigned value.
    /// </param>
    /// <returns>The written superblock (sequence assigned) on success.</returns>
    public Result<Superblock> Write(Superblock superblock)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(superblock);

        var targetSlot = _currentSlot == 0 ? 1 : 0;
        var assignedSequence = _lastSequence + 1;
        superblock.SuperblockSequence = assignedSequence;

        byte[] slotBytes;
        try
        {
            slotBytes = SuperblockSerializer.Serialize(superblock);
        }
        catch (ArgumentException ex)
        {
            return Result<Superblock>.Failure($"Superblock serialization failed: {ex.Message}");
        }

        try
        {
            _stream.Seek(targetSlot == 0 ? SlotAOffset : SlotBOffset, SeekOrigin.Begin);
            _stream.Write(slotBytes, 0, slotBytes.Length);
            _stream.Flush(flushToDisk: true);
        }
        catch (IOException ex)
        {
            return Result<Superblock>.Failure($"Superblock slot write failed: {ex.Message}");
        }

        FlushToDiskCount++;
        _current = superblock;
        _currentSlot = targetSlot;
        _lastSequence = assignedSequence;
        return Result<Superblock>.Success(superblock);
    }

    /// <summary>
    /// Reads and validates the slot at the given offset. Fails (without throwing)
    /// on a short/missing slot or when <see cref="SuperblockSerializer.Deserialize"/>
    /// rejects it (bad magic, checksum mismatch from a torn write, bad version).
    /// </summary>
    private Result<Superblock> ReadSlot(long offset)
    {
        if (_stream.Length < offset + SuperblockSerializer.SlotSize)
            return Result<Superblock>.Failure(
                $"File too short for superblock slot at offset {offset} (file length {_stream.Length}).");

        var buffer = new byte[SuperblockSerializer.SlotSize];
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.ReadExactly(buffer);
        return SuperblockSerializer.Deserialize(buffer);
    }

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
