namespace EmailDB.Format.V3;

/// <summary>
/// The disaster-only full-scan resolver (EmailDB_FileFormat_Spec.md Section 7):
/// the last link in the resolution precedence chain, consulted only when the
/// runtime map and the durable <see cref="BlockLocationIndex"/> both fail to
/// locate a block. It resolves a BlockId by performing a full forward scan of
/// the file (<see cref="BlockManager.ScanForward"/>), which fully verifies every
/// block and applies last-position-wins for duplicate BlockIds via a
/// <see cref="RuntimeBlockOffsetMap"/>.
///
/// <para><b>This is O(file) and expensive by design</b> — it exists for the case
/// where the durable index is missing or corrupt. The scan is performed lazily
/// once on first use and its rebuilt map is cached; <see cref="Rescan"/> forces
/// a fresh scan (e.g. after new blocks are appended). Every scan is announced on
/// the caller's <c>log</c>, so entering this path is never silent.</para>
///
/// Thread-safe: the lazy scan is guarded so concurrent readers share one rebuilt
/// map.
/// </summary>
public sealed class FullScanBlockIdResolver : IBlockIdResolver
{
    private readonly BlockManager _blockManager;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private RuntimeBlockOffsetMap? _scanned;

    /// <summary>
    /// Creates the disaster resolver over the file's block manager.
    /// </summary>
    /// <param name="blockManager">The block manager whose file is scanned to rebuild locations.</param>
    /// <param name="log">Optional sink notified each time a full scan is performed.</param>
    public FullScanBlockIdResolver(BlockManager blockManager, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(blockManager);
        _blockManager = blockManager;
        _log = log;
    }

    /// <summary>
    /// Resolves a BlockId from the rebuilt scan map, performing the full scan on
    /// first use. Returns false when the block is not present in the file or when
    /// the scan itself failed with an I/O error (the failure is logged); a
    /// corrupt block is simply absent from the rebuilt map (spec Section 13).
    /// </summary>
    /// <param name="blockId">ULID as 16 raw bytes, big-endian binary layout.</param>
    /// <param name="location">The block's latest location when found; null otherwise.</param>
    public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
    {
        var map = EnsureScanned();
        if (map is null)
        {
            location = null;
            return false;
        }
        return map.TryGetLocation(blockId, out location);
    }

    /// <summary>
    /// Discards the cached scan so the next resolution rebuilds the location map
    /// from a fresh forward scan — use after new blocks are appended or the file
    /// otherwise changes.
    /// </summary>
    public void Rescan()
    {
        lock (_gate)
        {
            _scanned = null;
        }
    }

    /// <summary>
    /// Returns the cached rebuilt map, performing the one disaster scan under the
    /// lock on first use. Returns null when the scan failed (logged), so the
    /// caller resolves nothing rather than throwing on the disaster path.
    /// </summary>
    private RuntimeBlockOffsetMap? EnsureScanned()
    {
        lock (_gate)
        {
            if (_scanned is not null)
                return _scanned;

            var rebuilt = new RuntimeBlockOffsetMap();
            var scan = _blockManager.ScanForward(rebuilt, _log);
            if (scan.IsFailure)
            {
                _log?.Invoke(
                    $"Disaster full-scan resolution failed with an I/O error; no block can be resolved " +
                    $"via the scan path: {scan.Error}");
                return null;
            }

            _log?.Invoke(
                $"Disaster full-scan resolution rebuilt {rebuilt.Count} block location(s) from a full file " +
                $"scan (spec Section 7); {scan.Value.DamagedRanges.Count} damaged range(s) skipped.");
            _scanned = rebuilt;
            return _scanned;
        }
    }
}
