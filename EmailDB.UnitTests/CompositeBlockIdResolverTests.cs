using System.Buffers.Binary;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for <see cref="CompositeBlockIdResolver"/> — the read-side resolution
/// precedence chain (US-EMDB-71-8, EmailDB_FileFormat_Spec.md Section 7): runtime
/// map first, then the durable location index, then (only as an explicitly
/// flagged/logged disaster path) the full file scan. Covers strict precedence,
/// fall-through, the disaster-path logging contract, and constructor validation.
/// </summary>
public class CompositeBlockIdResolverTests
{
    /// <summary>A 16-byte BlockId whose lexicographic order equals the numeric order of <paramref name="i"/>.</summary>
    private static byte[] BlockId(int i)
    {
        var id = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(12), i);
        return id;
    }

    /// <summary>An in-memory resolver over a fixed BlockId → location map.</summary>
    private sealed class StubResolver : IBlockIdResolver
    {
        private readonly Dictionary<string, BlockLocation> _map = new();

        public StubResolver Add(int i, long offset, long length)
        {
            var id = BlockId(i);
            _map[Convert.ToHexString(id)] = new BlockLocation
            {
                BlockId = id,
                Offset = offset,
                TotalBlockLength = length,
            };
            return this;
        }

        public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
        {
            if (_map.TryGetValue(Convert.ToHexString(blockId), out var loc))
            {
                location = loc;
                return true;
            }
            location = null;
            return false;
        }
    }

    /// <summary>
    /// An <see cref="IBlockIdResolver"/> that counts every resolution attempt and
    /// forwards to an inner resolver — a probing fake used to prove which tier a
    /// composite actually consulted (spec Section 7 precedence <i>order</i>), not
    /// merely which value came back.
    /// </summary>
    private sealed class CountingResolver : IBlockIdResolver
    {
        private readonly IBlockIdResolver _inner;
        public int Calls { get; private set; }

        public CountingResolver(IBlockIdResolver inner) => _inner = inner;

        public bool TryGetLocation(ReadOnlySpan<byte> blockId, out BlockLocation? location)
        {
            Calls++;
            return _inner.TryGetLocation(blockId, out location);
        }
    }

    [Fact]
    public void Runtime_hit_short_circuits_without_consulting_index_or_disaster()
    {
        // The SAME BlockId is present in all three tiers with a DIFFERENT offset in
        // each, so the returned value alone identifies the answering tier. The
        // counting probes then prove the lower tiers were never even asked.
        var runtime = new StubResolver().Add(1, offset: 100, length: 10);
        var index = new CountingResolver(new StubResolver().Add(1, offset: 999, length: 99));
        var disaster = new CountingResolver(new StubResolver().Add(1, offset: 555, length: 55));

        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { runtime, index }, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(BlockId(1), out var location));
        Assert.Equal(100, location!.Offset); // runtime map answered
        Assert.Equal(0, index.Calls);        // location index never consulted
        Assert.Equal(0, disaster.Calls);     // disaster scan never consulted
        Assert.Empty(logs);                  // no disaster fall-through announced
    }

    [Fact]
    public void Index_hit_resolves_from_index_without_triggering_the_scan()
    {
        // Runtime map misses; the location index and the disaster scan both hold
        // the key but at DIFFERENT offsets. The index must win by value AND the
        // disaster scan must never be touched (its counter stays at zero).
        var runtime = new CountingResolver(new StubResolver()); // empty
        var index = new CountingResolver(new StubResolver().Add(7, offset: 4096, length: 512));
        var disaster = new CountingResolver(new StubResolver().Add(7, offset: 8192, length: 512));

        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { runtime, index }, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(BlockId(7), out var location));
        Assert.Equal(4096, location!.Offset); // location index answered, not the scan
        Assert.Equal(1, runtime.Calls);       // runtime map was tried first
        Assert.Equal(1, index.Calls);         // then the index
        Assert.Equal(0, disaster.Calls);      // scan never triggered
        Assert.Empty(logs);                   // and never announced
    }

    [Fact]
    public void Only_absent_from_both_triggers_the_scan_exactly_once_and_logs()
    {
        // Present only in the disaster tier: the full scan is the sole resolver
        // that can answer. It must be consulted exactly once, and announced.
        var runtime = new CountingResolver(new StubResolver()); // empty
        var index = new CountingResolver(new StubResolver());   // empty
        var disaster = new CountingResolver(new StubResolver().Add(3, offset: 8192, length: 64));

        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { runtime, index }, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(BlockId(3), out var location));
        Assert.Equal(8192, location!.Offset); // disaster scan answered
        Assert.Equal(1, runtime.Calls);       // both ordinary tiers were tried
        Assert.Equal(1, index.Calls);
        Assert.Equal(1, disaster.Calls);      // scan consulted exactly once
        Assert.Single(logs);                  // and the disaster path was flagged
        Assert.Contains("disaster", logs[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_map_takes_precedence_over_location_index()
    {
        // Same BlockId is in both; the runtime map (first link) must win.
        var runtime = new StubResolver().Add(1, offset: 100, length: 10);
        var locationIndex = new StubResolver().Add(1, offset: 999, length: 99);
        var resolver = new CompositeBlockIdResolver(new IBlockIdResolver[] { runtime, locationIndex });

        Assert.True(resolver.TryGetLocation(BlockId(1), out var location));
        Assert.Equal(100, location!.Offset);
        Assert.Equal(10, location.TotalBlockLength);
    }

    [Fact]
    public void Falls_through_to_location_index_when_runtime_map_misses()
    {
        var runtime = new StubResolver(); // empty
        var locationIndex = new StubResolver().Add(7, offset: 4096, length: 512);
        var resolver = new CompositeBlockIdResolver(new IBlockIdResolver[] { runtime, locationIndex });

        Assert.True(resolver.TryGetLocation(BlockId(7), out var location));
        Assert.Equal(4096, location!.Offset);
        Assert.Equal(512, location.TotalBlockLength);
    }

    [Fact]
    public void Disaster_scan_is_used_only_when_chain_misses_and_is_logged()
    {
        var runtime = new StubResolver();
        var locationIndex = new StubResolver();
        var disaster = new StubResolver().Add(3, offset: 8192, length: 64);

        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { runtime, locationIndex }, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(BlockId(3), out var location));
        Assert.Equal(8192, location!.Offset);
        // The disaster path must be explicitly flagged.
        Assert.Single(logs);
        Assert.Contains("disaster", logs[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disaster_scan_not_consulted_when_chain_hits()
    {
        var runtime = new StubResolver().Add(5, offset: 1, length: 1);
        var locationIndex = new StubResolver();
        var disaster = new StubResolver().Add(5, offset: 777, length: 7);

        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { runtime, locationIndex }, disaster, logs.Add);

        Assert.True(resolver.TryGetLocation(BlockId(5), out var location));
        Assert.Equal(1, location!.Offset);
        Assert.Empty(logs); // never fell through to the disaster path
    }

    [Fact]
    public void All_miss_without_disaster_scan_returns_false_and_does_not_log()
    {
        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { new StubResolver(), new StubResolver() },
            disasterScan: null, log: logs.Add);

        Assert.False(resolver.TryGetLocation(BlockId(42), out var location));
        Assert.Null(location);
        Assert.Empty(logs);
    }

    [Fact]
    public void All_miss_with_disaster_scan_returns_false_when_disaster_also_misses()
    {
        var logs = new List<string>();
        var resolver = new CompositeBlockIdResolver(
            new IBlockIdResolver[] { new StubResolver() },
            disasterScan: new StubResolver(), log: logs.Add);

        Assert.False(resolver.TryGetLocation(BlockId(42), out var location));
        Assert.Null(location);
        Assert.Single(logs); // it still entered (and flagged) the disaster path
    }

    [Fact]
    public void Empty_chain_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new CompositeBlockIdResolver(Array.Empty<IBlockIdResolver>()));
    }

    [Fact]
    public void Null_resolver_in_chain_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new CompositeBlockIdResolver(new IBlockIdResolver?[] { new StubResolver(), null }!));
    }
}
