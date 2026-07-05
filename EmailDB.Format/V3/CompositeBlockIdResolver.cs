namespace EmailDB.Format.V3;

/// <summary>
/// The read-side resolution precedence chain (EmailDB_FileFormat_Spec.md
/// Section 7): resolves a BlockId to its latest location by consulting a fixed
/// ordered list of <see cref="IBlockIdResolver"/>s and returning the first hit.
/// The canonical chain is <see cref="RuntimeBlockOffsetMap"/> (blocks appended
/// this session, not yet checkpointed) first, then the durable
/// <see cref="BlockLocationIndex"/> (O(log n)), and — only when both miss — the
/// <b>disaster-only full file scan</b> (<see cref="FullScanBlockIdResolver"/>),
/// which is O(file) and means the durable index is unavailable or does not yet
/// contain the block.
///
/// <para>The disaster path is kept SEPARATE from the ordinary chain and is
/// explicitly flagged: whenever resolution falls through to it, a message is
/// emitted to the caller's <c>log</c> so the event is never silent (spec
/// Section 7: full scan is a last resort). Construct the canonical chain with
/// <see cref="Create"/>; the general constructor accepts any ordered chain so
/// callers can compose additional resolvers (e.g. a compaction snapshot).</para>
///
/// Thread-safety follows the underlying resolvers: the runtime map is
/// thread-safe, a <see cref="BlockLocationIndex"/> snapshot is read-only, so a
/// composite over those is safe for concurrent readers.
/// </summary>
public sealed class CompositeBlockIdResolver : IBlockIdResolver
{
    private readonly IBlockIdResolver[] _precedenceChain;
    private readonly IBlockIdResolver? _disasterScan;
    private readonly Action<string>? _log;

    /// <summary>
    /// Composes an ordered precedence chain with an optional disaster-only
    /// fallback. Each resolver in <paramref name="precedenceChain"/> is tried in
    /// order; only if every one misses is <paramref name="disasterScan"/>
    /// consulted (and the fall-through logged).
    /// </summary>
    /// <param name="precedenceChain">Ordinary resolvers in strict precedence order (spec Section 7: runtime map, then location index). Must be non-empty and contain no nulls.</param>
    /// <param name="disasterScan">The last-resort full-scan resolver, or null to fail resolution when the chain misses.</param>
    /// <param name="log">Optional sink notified once each time resolution falls through to the disaster scan.</param>
    /// <exception cref="ArgumentException">The chain is empty or contains a null resolver.</exception>
    public CompositeBlockIdResolver(
        IReadOnlyList<IBlockIdResolver> precedenceChain,
        IBlockIdResolver? disasterScan = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(precedenceChain);
        if (precedenceChain.Count == 0)
            throw new ArgumentException("The precedence chain must contain at least one resolver.", nameof(precedenceChain));

        var chain = new IBlockIdResolver[precedenceChain.Count];
        for (int i = 0; i < precedenceChain.Count; i++)
        {
            chain[i] = precedenceChain[i]
                ?? throw new ArgumentException($"Resolver at index {i} is null.", nameof(precedenceChain));
        }

        _precedenceChain = chain;
        _disasterScan = disasterScan;
        _log = log;
    }

    /// <summary>
    /// Builds the canonical spec Section 7 chain: runtime map first, then the
    /// durable location index, then (optionally) the disaster-only full scan.
    /// </summary>
    /// <param name="runtimeMap">Blocks appended this session, not yet checkpointed — resolved first.</param>
    /// <param name="locationIndex">The durable BlockLocationIndex — resolved second.</param>
    /// <param name="disasterScan">The last-resort full-scan resolver, or null.</param>
    /// <param name="log">Optional sink notified when the disaster path is entered.</param>
    public static CompositeBlockIdResolver Create(
        RuntimeBlockOffsetMap runtimeMap,
        BlockLocationIndex locationIndex,
        IBlockIdResolver? disasterScan = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeMap);
        ArgumentNullException.ThrowIfNull(locationIndex);
        return new CompositeBlockIdResolver(
            new IBlockIdResolver[] { runtimeMap, locationIndex }, disasterScan, log);
    }

    /// <summary>
    /// Resolves a BlockId in strict precedence order, returning the first
    /// resolver's hit. Only when every ordinary resolver misses is the
    /// disaster-only full scan consulted (logged as it happens); when no
    /// disaster scan was supplied, an all-miss resolution simply returns false.
    /// </summary>
    /// <param name="blockId">ULID as 16 raw bytes, big-endian binary layout.</param>
    /// <param name="location">The block's latest location when found; null otherwise.</param>
    public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
    {
        foreach (var resolver in _precedenceChain)
        {
            if (resolver.TryGetLocation(blockId, out location))
                return true;
        }

        location = null;
        if (_disasterScan is null)
            return false;

        _log?.Invoke(
            "BlockId resolution fell through the runtime map and the location index; entering the " +
            "disaster-only full-scan path (spec Section 7). This is O(file) and indicates the durable " +
            "location index is unavailable or does not yet contain the requested block.");
        return _disasterScan.TryGetLocation(blockId, out location);
    }
}
