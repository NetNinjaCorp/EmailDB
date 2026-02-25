using System.Buffers.Binary;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.Format.FileManagement;

/// <summary>
/// Buffers B+-tree index inserts into an in-memory WAL buffer and batch-flushes
/// to the BTreeIndex to minimize write amplification.
/// Entries are persisted to the WAL region on disk during InsertAsync so they
/// survive process crashes. On construction, any unflushed entries are recovered
/// from disk into the in-memory buffer (crash recovery path).
/// Entries are not visible in the B+-tree until FlushAsync is called (or auto-flush triggers).
/// </summary>
public class BTreeWALManager : IDisposable
{
    private readonly RawBlockManager _rawBlockManager;
    private readonly BTreeIndex _btreeIndex;
    private readonly long _walRegionOffset;
    private readonly int _autoFlushThreshold;
    private readonly bool _autoFlushEnabled;
    private readonly AsyncReaderWriterLock _walLock = new();

    private readonly Dictionary<EmailHashedID, (long BlockOffset, long BlockId)> _buffer = new();
    private bool _dirty;
    private long _flushSequence;
    private int _diskEntryCount; // tracks how many entries are on disk (for append position)

    // Time-based auto-flush fields
    private readonly PeriodicTimer? _periodicTimer;
    private readonly CancellationTokenSource? _timerCts;
    private readonly Task? _timerLoopTask;

    /// <summary>
    /// WAL on-disk header layout (22 bytes):
    ///   EntryCount  (int32, 4 bytes)  — number of 48-byte entries on disk
    ///   Dirty       (byte,  1 byte)   — 1 if unflushed entries exist
    ///   FlushSeq    (int64, 8 bytes)  — monotonic flush counter
    ///   Reserved    (9 bytes)         — zero-filled for future use
    /// </summary>
    public const int WALHeaderSize = 22;

    /// <summary>Each WAL entry is 48 bytes (matches LeafEntry.Size).</summary>
    public const int WALEntrySize = 48;

    /// <summary>Number of entries currently buffered in memory.</summary>
    public int BufferCount => _buffer.Count;

    /// <summary>True if the buffer has entries that have not been flushed to the B+-tree.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Incremented on each successful flush.</summary>
    public long FlushSequence => _flushSequence;

    public BTreeWALManager(
        RawBlockManager rawBlockManager,
        BTreeIndex btreeIndex,
        long walPayloadFileOffset,
        int autoFlushThreshold = 82,
        bool autoFlushEnabled = true,
        TimeSpan? autoFlushInterval = null)
    {
        _rawBlockManager = rawBlockManager;
        _btreeIndex = btreeIndex;
        _walRegionOffset = walPayloadFileOffset;
        _autoFlushThreshold = autoFlushThreshold;
        _autoFlushEnabled = autoFlushEnabled;

        // Crash recovery: read WAL header and recover any unflushed entries from disk
        RecoverFromDisk().GetAwaiter().GetResult();

        if (autoFlushInterval.HasValue)
        {
            _timerCts = new CancellationTokenSource();
            _periodicTimer = new PeriodicTimer(autoFlushInterval.Value);
            _timerLoopTask = RunTimerFlushLoopAsync(_timerCts.Token);
        }
    }

    /// <summary>
    /// Adds an entry to the in-memory WAL buffer and persists it to the WAL region on disk.
    /// Duplicate keys are upserted (value updated without creating a new entry).
    /// If auto-flush is enabled and the buffer reaches the threshold, triggers FlushAsync.
    /// </summary>
    public async Task InsertAsync(EmailHashedID key, long blockOffset, long blockId, CancellationToken ct = default)
    {
        await _walLock.AcquireWriterLock(ct);
        try
        {
            bool isNewKey = !_buffer.ContainsKey(key);
            _buffer[key] = (blockOffset, blockId);
            _dirty = true;

            if (isNewKey)
            {
                // Append new 48-byte entry to disk at the next slot
                var entryBytes = SerializeEntry(key, blockOffset, blockId);
                long entryOffset = _walRegionOffset + WALHeaderSize + (_diskEntryCount * WALEntrySize);
                await _rawBlockManager.WriteRawBytesAsync(entryBytes, entryOffset, ct);
                _diskEntryCount++;
            }
            else
            {
                // Upsert: rewrite all entries to disk to keep deduped state consistent
                await RewriteAllEntriesToDiskAsync(ct);
            }

            // Update WAL header on disk
            await WriteWALHeaderAsync(ct);

            if (_autoFlushEnabled && _buffer.Count >= _autoFlushThreshold)
            {
                await FlushInternalAsync(ct);
            }
        }
        finally
        {
            _walLock.ReleaseWriterLock();
        }
    }

    /// <summary>
    /// Looks up a key: checks the in-memory WAL buffer first, then falls through
    /// to BTreeIndex.LookupAsync if not found in the buffer.
    /// </summary>
    public async Task<Result<LeafEntry>> LookupAsync(EmailHashedID key, CancellationToken ct = default)
    {
        await _walLock.AcquireReaderLock(ct);
        try
        {
            if (_buffer.TryGetValue(key, out var entry))
            {
                return Result<LeafEntry>.Success(new LeafEntry
                {
                    Key = key,
                    BlockOffset = entry.BlockOffset,
                    BlockId = entry.BlockId
                });
            }
        }
        finally
        {
            _walLock.ReleaseReaderLock();
        }

        return await _btreeIndex.LookupAsync(key, ct);
    }

    /// <summary>
    /// Flushes all buffered entries to the B+-tree, clears the buffer,
    /// marks dirty flag as false, and increments the flush sequence.
    /// Also clears the WAL region on disk.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _walLock.AcquireWriterLock(ct);
        try
        {
            await FlushInternalAsync(ct);
        }
        finally
        {
            _walLock.ReleaseWriterLock();
        }
    }

    private async Task FlushInternalAsync(CancellationToken ct)
    {
        if (_buffer.Count == 0) return;

        // Sort buffered entries by key for optimal BTree insertion order
        var sorted = _buffer.OrderBy(kv => kv.Key).ToList();

        foreach (var kv in sorted)
        {
            var result = await _btreeIndex.InsertAsync(
                kv.Key, kv.Value.BlockOffset, kv.Value.BlockId, ct);
            if (result.IsFailure)
                throw new InvalidOperationException(
                    $"Failed to flush WAL entry to BTree: {result.Error}");
        }

        _buffer.Clear();
        _dirty = false;
        _flushSequence++;
        _diskEntryCount = 0;

        // Clear WAL region on disk: rewrite header with EntryCount=0, dirty=0, FlushSequence++
        await WriteWALHeaderAsync(ct);
    }

    #region WAL Disk I/O

    /// <summary>
    /// Reads the WAL header from disk and recovers any unflushed entries
    /// into the in-memory buffer. Called from the constructor.
    /// </summary>
    private async Task RecoverFromDisk()
    {
        // Check if the file has enough data for a WAL header at _walRegionOffset
        if (_rawBlockManager.FileLength < _walRegionOffset + WALHeaderSize)
            return;

        var headerBytes = await _rawBlockManager.ReadRawBytesAsync(_walRegionOffset, WALHeaderSize);
        if (headerBytes.Length < WALHeaderSize)
            return;

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(0, 4));
        byte dirtyFlag = headerBytes[4];
        long flushSeq = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.AsSpan(5, 8));

        // Validate header values — reject garbage data (e.g. overlapping block data)
        const int maxReasonableEntries = 349525; // 16MB WAL region / 48 bytes per entry
        if (entryCount < 0 || entryCount > maxReasonableEntries)
            return; // Not a valid WAL header
        if (dirtyFlag > 1)
            return; // Not a valid WAL header (dirty flag must be 0 or 1)
        if (flushSeq < 0)
            return; // Not a valid WAL header

        _flushSequence = flushSeq;

        if (dirtyFlag == 0 || entryCount == 0)
            return; // Nothing to recover

        // Read all WAL entries from disk
        int totalEntryBytes = entryCount * WALEntrySize;
        long entriesOffset = _walRegionOffset + WALHeaderSize;
        var entryData = await _rawBlockManager.ReadRawBytesAsync(entriesOffset, totalEntryBytes);
        if (entryData.Length < totalEntryBytes)
            return; // Truncated — can't recover

        for (int i = 0; i < entryCount; i++)
        {
            int offset = i * WALEntrySize;
            var span = entryData.AsSpan(offset, WALEntrySize);
            var key = new EmailHashedID(
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(24, 8)));
            long blockOffset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(32, 8));
            long blockId = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(40, 8));

            _buffer[key] = (blockOffset, blockId);
        }

        _dirty = true;
        _diskEntryCount = entryCount;
    }

    /// <summary>Writes the 22-byte WAL header to disk at _walRegionOffset.</summary>
    private async Task WriteWALHeaderAsync(CancellationToken ct = default)
    {
        var header = new byte[WALHeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), _diskEntryCount);
        header[4] = _dirty ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5, 8), _flushSequence);
        // bytes 13..21 remain zero (reserved)
        await _rawBlockManager.WriteRawBytesAsync(header, _walRegionOffset, ct);
    }

    /// <summary>Serializes a single WAL entry (48 bytes).</summary>
    private static byte[] SerializeEntry(EmailHashedID key, long blockOffset, long blockId)
    {
        var buf = new byte[WALEntrySize];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), key.Part1);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(8, 8), key.Part2);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(16, 8), key.Part3);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(24, 8), key.Part4);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32, 8), blockOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40, 8), blockId);
        return buf;
    }

    /// <summary>
    /// Rewrites all buffered entries to disk (used after upsert to maintain deduped state).
    /// </summary>
    private async Task RewriteAllEntriesToDiskAsync(CancellationToken ct)
    {
        int i = 0;
        foreach (var kv in _buffer)
        {
            var entryBytes = SerializeEntry(kv.Key, kv.Value.BlockOffset, kv.Value.BlockId);
            long entryOffset = _walRegionOffset + WALHeaderSize + (i * WALEntrySize);
            await _rawBlockManager.WriteRawBytesAsync(entryBytes, entryOffset, ct);
            i++;
        }
        _diskEntryCount = _buffer.Count;
    }

    #endregion

    private async Task RunTimerFlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _periodicTimer!.WaitForNextTickAsync(ct))
            {
                await _walLock.AcquireWriterLock(ct);
                try
                {
                    if (_dirty)
                        await FlushInternalAsync(ct);
                }
                finally
                {
                    _walLock.ReleaseWriterLock();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal
        }
    }

    public void Dispose()
    {
        _timerCts?.Cancel();
        _periodicTimer?.Dispose();
        _timerCts?.Dispose();
        _walLock.Dispose();
    }
}
