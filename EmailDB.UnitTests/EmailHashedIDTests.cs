using System.Text;
// Disambiguate from the legacy global-namespace EmailHashedID (v2 model).
using V3Id = EmailDB.Format.V3.EmailHashedID;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests the canonical v3 <see cref="V3Id"/> (US-EMDB-80-5): SHA3-256 over raw
/// MIME content bytes, stable/deterministic across sessions, byte-order consistent with
/// the PrimaryEmail B+-tree key ordering, and the dedupe lookup helper.
/// </summary>
public class EmailHashedIDTests
{
    // FIPS-202 SHA3-256 known-answer vectors (NIST): empty message and "abc".
    private const string Sha3EmptyHex =
        "a7ffc6f8bf1ed76651c14756a061d662f580ff4de43b49fa82d80a4b80f8434a";
    private const string Sha3AbcHex =
        "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532";

    [Fact]
    public void ComputeFromRawContent_EmptyInput_MatchesNistSha3Vector()
    {
        var id = V3Id.ComputeFromRawContent(ReadOnlySpan<byte>.Empty);
        Assert.Equal(Sha3EmptyHex, id.ToString());
    }

    [Fact]
    public void ComputeFromRawContent_Abc_MatchesNistSha3Vector()
    {
        var id = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(Sha3AbcHex, id.ToString());
    }

    // Frozen golden vector for a realistic RFC 5322 message. This constant is committed to
    // source: any process, session, machine, or runtime version that hashes the exact same
    // MIME octets below MUST reproduce this digest. If a future change to the hash path
    // (algorithm, byte handling, line-ending normalization) ever altered the stored ID, this
    // assertion fails — locking EmailHashedID stability across sessions/process restarts.
    private const string GoldenMimeIdHex =
        "c746d5c689fab3fb30666c23562e99f09129b89e38a808a359f904e3e1fc018c";
    private static readonly byte[] GoldenMime = Encoding.UTF8.GetBytes(
        "From: Alice <alice@example.com>\r\n" +
        "To: Bob <bob@example.com>\r\n" +
        "Subject: Golden vector\r\n" +
        "Date: Mon, 01 Jan 2024 00:00:00 +0000\r\n" +
        "Message-ID: <golden-001@example.com>\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "This message is frozen so its EmailHashedID never drifts.\r\n");

    [Fact]
    public void ComputeFromRawContent_RealisticMime_MatchesFrozenGoldenId()
    {
        // Cross-session stability: the expected value is a pre-committed constant, so a fresh
        // process computing this must yield the identical stored 32-byte ID.
        var id = V3Id.ComputeFromRawContent(GoldenMime);
        Assert.Equal(GoldenMimeIdHex, id.ToString());
        Assert.Equal(Convert.FromHexString(GoldenMimeIdHex), id.GetBytes());
    }

    [Fact]
    public void FromHex_OfFrozenGoldenId_EqualsFreshlyComputedId()
    {
        // The stored/persisted form (hex round-tripped through the byte constructor, exactly
        // as a fresh session would rehydrate a key from the B+-tree) equals a freshly hashed ID.
        var stored = V3Id.FromHex(GoldenMimeIdHex);
        var computed = V3Id.ComputeFromRawContent(GoldenMime);
        Assert.Equal(stored, computed);
        Assert.Equal(0, stored.CompareTo(computed));
    }

    [Fact]
    public void ComputeFromRawContent_Is32Bytes()
    {
        var id = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("hello"));
        Assert.Equal(32, id.GetBytes().Length);
        Assert.Equal(32, V3Id.Size);
    }

    [Fact]
    public void ComputeFromRawContent_IsDeterministicAcrossCalls()
    {
        var mime = Encoding.UTF8.GetBytes(
            "From: a@x.com\r\nTo: b@y.com\r\nSubject: Hi\r\n\r\nBody text.\r\n");
        var a = V3Id.ComputeFromRawContent(mime);
        var b = V3Id.ComputeFromRawContent(mime);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void ComputeFromRawContent_DifferentBytes_DifferentId()
    {
        var a = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("abc"));
        var b = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("abd"));
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void GetBytes_RoundTripsThroughSpanConstructor()
    {
        var id = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("round trip"));
        var bytes = id.GetBytes();
        var rebuilt = new V3Id(bytes);
        Assert.Equal(id, rebuilt);
        Assert.Equal(bytes, rebuilt.GetBytes());
    }

    [Fact]
    public void GetBytes_PreservesDigestByteOrder()
    {
        // Digest of "abc" must be stored/returned in natural big-endian order.
        var id = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(Convert.FromHexString(Sha3AbcHex), id.GetBytes());
    }

    [Fact]
    public void CompareTo_MatchesByteWiseLexicographicOrder()
    {
        // The B+-tree compares keys with SequenceCompareTo; CompareTo must agree.
        for (int i = 0; i < 50; i++)
        {
            var x = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes($"msg-{i}"));
            var y = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes($"msg-{i + 1}"));
            int structCmp = Math.Sign(x.CompareTo(y));
            int byteCmp = Math.Sign(x.GetBytes().AsSpan().SequenceCompareTo(y.GetBytes()));
            Assert.Equal(byteCmp, structCmp);
        }
    }

    [Fact]
    public void Constructor_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new V3Id(new byte[31]));
        Assert.Throws<ArgumentException>(() => new V3Id(new byte[33]));
    }

    [Fact]
    public void Empty_IsAllZeroSentinel()
    {
        Assert.True(V3Id.Empty.IsEmpty);
        Assert.True(default(V3Id).IsEmpty);
        Assert.False(V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("x")).IsEmpty);
    }

    [Fact]
    public void FromHex_RoundTripsToString()
    {
        var id = V3Id.ComputeFromRawContent(Encoding.ASCII.GetBytes("hex round trip"));
        var parsed = V3Id.FromHex(id.ToString());
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void IsDuplicate_ReturnsTrue_WhenIndexContainsId()
    {
        var mime = Encoding.ASCII.GetBytes("duplicate me");
        var known = V3Id.ComputeFromRawContent(mime);

        // Simulate a PrimaryEmail index holding exactly one ID.
        var index = new HashSet<V3Id> { known };

        bool dup = V3Id.IsDuplicate(mime, index.Contains, out var id);

        Assert.True(dup);
        Assert.Equal(known, id);
    }

    [Fact]
    public void IsDuplicate_ReturnsFalse_WhenIndexMissingId()
    {
        var index = new HashSet<V3Id>();
        bool dup = V3Id.IsDuplicate(
            Encoding.ASCII.GetBytes("brand new"), index.Contains, out var id);

        Assert.False(dup);
        Assert.False(id.IsEmpty);
    }

    [Fact]
    public void IsDuplicate_UsedAsBTreeKey_MatchesGetBytes()
    {
        // The dedupe helper's ID is exactly the 32-byte key a caller would probe the tree with.
        var mime = Encoding.ASCII.GetBytes("key check");
        V3Id captured = default;
        V3Id.IsDuplicate(mime, probeId => { captured = probeId; return false; }, out var id);

        Assert.Equal(id, captured);
        Assert.Equal(V3Id.ComputeFromRawContent(mime).GetBytes(), id.GetBytes());
    }
}
