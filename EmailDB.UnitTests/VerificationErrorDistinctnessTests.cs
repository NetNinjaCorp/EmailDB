using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Dedicated criterion test for story US-EMDB-75's acceptance:
/// <b>"Corruption and wrong-key and tamper surface as distinct error types."</b>
///
/// <para>Where <see cref="VerificationErrorTaxonomyTests"/> proves the taxonomy holds
/// under pure construction, this suite proves distinctness <b>end-to-end through the
/// real failure paths</b>: a corrupted on-disk block payload surfaces a
/// <see cref="CorruptionError"/> (<see cref="VerificationFailureKind.Corruption"/>) and
/// is provably NOT the other two kinds; a tampered Merkle child hash surfaces an
/// <see cref="IntegrityError"/> (<see cref="VerificationFailureKind.Integrity"/>) and is
/// provably NOT the other two; and — because v3 has no decrypt call site yet (encryption
/// is a future seam) — the <see cref="WrongKeyOrTamperError"/> class is proven distinct
/// at the taxonomy boundary: its factory paths yield
/// <see cref="VerificationFailureKind.WrongKeyOrTamper"/>, it is catchable separately from
/// the other two, and it carries KeyEpoch context.</para>
///
/// <para>The load-bearing assertion is <b>mutual exclusivity under polymorphic dispatch</b>:
/// catch the shared <see cref="VerificationError"/> base, <c>switch</c> on
/// <see cref="VerificationError.Kind"/>, and confirm each of the three sources routes to
/// exactly one arm — through the two real paths plus the tamper boundary.</para>
/// </summary>
public class VerificationErrorDistinctnessTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-distinct-{Guid.NewGuid():N}.emdb");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private FileStream OpenStream() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private BlockManager CreateManager(IBlockOffsetMap? offsetMap = null) =>
        new(OpenStream(), Superblock.DefaultMaxPayloadLength, offsetMap: offsetMap, firstBlockOffset: 0, ownsStream: true);

    private static byte[] SamplePayload(int length, int seed = 1) =>
        Enumerable.Range(0, length).Select(i => (byte)(i * 7 + seed)).ToArray();

    private void CorruptByte(long offset)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        var b = (byte)fs.ReadByte();
        fs.Seek(offset, SeekOrigin.Begin);
        fs.WriteByte((byte)(b ^ 0xFF));
    }

    private static byte[] FlipLast(byte[] hash)
    {
        var copy = (byte[])hash.Clone();
        copy[^1] ^= 0x01;
        return copy;
    }

    /// <summary>
    /// Drives a real corrupted-payload read and returns the surfaced VerificationError.
    /// A fresh manager reads the on-disk corruption without the writer's read buffer
    /// masking it.
    /// </summary>
    private VerificationError SurfaceRealCorruption()
    {
        BlockLocation location;
        using (var writer = CreateManager())
        {
            location = writer.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, SamplePayload(96)).Value;
            writer.Flush();
        }

        CorruptByte(location.Offset + BlockSerializer.PayloadOffset + 8); // damage payload bytes

        using var reader = CreateManager(offsetMap: new RuntimeBlockOffsetMap());
        var read = reader.Read(location.Offset);
        Assert.True(read.IsFailure);
        Assert.NotNull(read.VerificationError);
        return read.VerificationError!;
    }

    /// <summary>
    /// Drives a real Merkle child-hash tamper against a verified node read and returns
    /// the surfaced VerificationError.
    /// </summary>
    private VerificationError SurfaceRealIntegrityTamper()
    {
        var offsetMap = new RuntimeBlockOffsetMap();
        using var manager = CreateManager(offsetMap: offsetMap);
        var store = new BTreeNodeStore(manager, offsetMap);

        var payload = SamplePayload(EmailDB.Format.V3.BTreeNodeSerializer.NodeHeaderSize + 16);
        var nodeRef = store.Append(BTreeNodeKind.Leaf, payload, BTreeChildAddressing.Offset).Value;
        manager.Flush();

        var tampered = new BTreeNodeRef
        {
            Addressing = nodeRef.Addressing,
            Reference = nodeRef.Reference,
            NodeHash = FlipLast(nodeRef.NodeHash), // parent vouches for a hash the node no longer matches
        };
        var read = store.ReadVerified(tampered, BTreeNodeKind.Leaf);
        Assert.True(read.IsFailure);
        Assert.NotNull(read.VerificationError);
        return read.VerificationError!;
    }

    // ---- Real path: a corrupt payload surfaces Corruption, and is NOT the other kinds ----

    [Fact]
    public void RealCorruptPayload_SurfacesCorruptionKind_AndNotTheOthers()
    {
        var error = SurfaceRealCorruption();

        Assert.IsType<CorruptionError>(error);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);

        // Distinctness: this real failure is neither of the other two classes/kinds.
        Assert.False(error is WrongKeyOrTamperError);
        Assert.False(error is IntegrityError);
        Assert.NotEqual(VerificationFailureKind.WrongKeyOrTamper, error.Kind);
        Assert.NotEqual(VerificationFailureKind.Integrity, error.Kind);
    }

    // ---- Real path: a Merkle tamper surfaces Integrity, and is NOT the other kinds ----

    [Fact]
    public void RealMerkleTamper_SurfacesIntegrityKind_AndNotTheOthers()
    {
        var error = SurfaceRealIntegrityTamper();

        Assert.IsType<IntegrityError>(error);
        Assert.Equal(VerificationFailureKind.Integrity, error.Kind);

        // Distinctness: a structural integrity mismatch is neither raw corruption nor tamper.
        Assert.False(error is CorruptionError);
        Assert.False(error is WrongKeyOrTamperError);
        Assert.NotEqual(VerificationFailureKind.Corruption, error.Kind);
        Assert.NotEqual(VerificationFailureKind.WrongKeyOrTamper, error.Kind);
    }

    // ---- Taxonomy boundary: WrongKeyOrTamper is a separately catchable class carrying KeyEpoch ----

    [Fact]
    public void WrongKeyOrTamper_IsCatchableSeparately_AndCarriesKeyEpoch()
    {
        // v3 has no decrypt call site yet (encryption is a future seam), so distinctness
        // is proven at the factory boundary the future decrypt path will raise from.
        var gcm = WrongKeyOrTamperError.GcmTagFailure(offset: 4096, keyEpoch: 5);
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, gcm.Kind);
        Assert.Equal(TamperCause.GcmTag, gcm.Cause);
        Assert.Equal(5, gcm.KeyEpoch); // "never brute other epochs" context is carried

        var token = WrongKeyOrTamperError.TokenMismatch(keyEpoch: 9);
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, token.Kind);
        Assert.Equal(9, token.KeyEpoch);

        // Catchable specifically as WrongKeyOrTamperError...
        var caught = Assert.Throws<WrongKeyOrTamperError>(void () => throw gcm);
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, caught.Kind);

        // ...and a real CorruptionError is NOT swallowed by that catch clause (distinct type).
        var corruption = SurfaceRealCorruption();
        Assert.False(corruption is WrongKeyOrTamperError);
        Assert.ThrowsAny<CorruptionError>(void () => throw corruption);
    }

    // ---- The load-bearing test: polymorphic catch + switch on Kind routes each to one arm ----

    [Fact]
    public void ThreeSources_AreMutuallyExclusive_UnderPolymorphicKindSwitch()
    {
        // Two real failure paths + the tamper boundary (no decrypt call site yet in v3).
        VerificationError[] surfaced =
        [
            SurfaceRealCorruption(),
            SurfaceRealIntegrityTamper(),
            WrongKeyOrTamperError.GcmTagFailure(offset: 4096, keyEpoch: 1),
        ];

        var buckets = new Dictionary<VerificationFailureKind, int>
        {
            [VerificationFailureKind.Corruption] = 0,
            [VerificationFailureKind.WrongKeyOrTamper] = 0,
            [VerificationFailureKind.Integrity] = 0,
        };

        foreach (VerificationError error in surfaced)
        {
            // Branch on Kind alone — no reflection, no concrete-type dependence — and
            // assert the concrete type agrees, so the switch is provably unambiguous.
            switch (error.Kind)
            {
                case VerificationFailureKind.Corruption:
                    Assert.IsType<CorruptionError>(error);
                    break;
                case VerificationFailureKind.WrongKeyOrTamper:
                    Assert.IsType<WrongKeyOrTamperError>(error);
                    break;
                case VerificationFailureKind.Integrity:
                    Assert.IsType<IntegrityError>(error);
                    break;
                default:
                    Assert.Fail($"Unexpected verification kind {error.Kind}");
                    break;
            }

            buckets[error.Kind]++;
        }

        // Each of the three distinct kinds was hit exactly once: mutually exclusive, complete.
        Assert.All(buckets.Values, count => Assert.Equal(1, count));
        Assert.Equal(3, surfaced.Select(e => e.Kind).Distinct().Count());
    }
}
