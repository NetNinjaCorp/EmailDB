using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the v3 corruption-handling error taxonomy (US-EMDB-75-5,
/// EmailDB_FileFormat_Spec.md Section 13): the three distinct failure classes —
/// <see cref="CorruptionError"/>, <see cref="WrongKeyOrTamperError"/>, and
/// <see cref="IntegrityError"/> — construct with the right <see cref="VerificationFailureKind"/>,
/// carry offset / BlockId / damaged-range context, are catchably distinct through the
/// shared <see cref="VerificationError"/> base, and compose with the v3
/// <see cref="Result{T}"/> convention. Underpins the story's acceptance that
/// corruption vs wrong-key/tamper vs integrity surface as distinct error types with
/// offsets and BlockIds.
/// </summary>
public class VerificationErrorTaxonomyTests
{
    private static byte[] SampleBlockId() =>
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
    ];

    // ---------------------------------------------------------------- Kind mapping

    [Fact]
    public void CorruptionError_ReportsCorruptionKind()
    {
        var error = CorruptionError.HeaderChecksumMismatch(4096);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
    }

    [Fact]
    public void WrongKeyOrTamperError_ReportsWrongKeyOrTamperKind()
    {
        var error = WrongKeyOrTamperError.GcmTagFailure(SampleBlockId(), offset: 4096, keyEpoch: 2);
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, error.Kind);
    }

    [Fact]
    public void IntegrityError_ReportsIntegrityKind()
    {
        var error = IntegrityError.MerkleChildHashMismatch(SampleBlockId(), offset: 8192);
        Assert.Equal(VerificationFailureKind.Integrity, error.Kind);
    }

    // -------------------------------------------------------- Context propagation

    [Fact]
    public void HeaderChecksumMismatch_CarriesOffsetAndDamagedRange()
    {
        var range = new DamagedRange(4096, 8192, "header checksum mismatch");
        var error = CorruptionError.HeaderChecksumMismatch(4096, range);

        Assert.Equal(CorruptionCause.HeaderChecksum, error.Cause);
        Assert.Equal(4096, error.Offset);
        Assert.Null(error.BlockId);
        Assert.Equal(range, error.DamagedRange);
        Assert.Equal(4096, error.DamagedRange!.Value.Start);
        Assert.Equal(8192, error.DamagedRange!.Value.End);
        Assert.Equal(4096, error.DamagedRange!.Value.Length);
        Assert.False(error.ReferencedLiveData);
    }

    [Fact]
    public void ReferencedDataLoss_NamesTheAffectedBlockId()
    {
        var id = SampleBlockId();
        var error = CorruptionError.ReferencedDataLoss(id, offset: 12288);

        Assert.Equal(CorruptionCause.ReferencedLiveDataLoss, error.Cause);
        Assert.True(error.ReferencedLiveData);
        Assert.Equal(12288, error.Offset);
        Assert.NotNull(error.BlockId);
        Assert.Equal(id, error.BlockId);
        Assert.Contains(Convert.ToHexStringLower(id), error.Message);
        Assert.Equal(Convert.ToHexStringLower(id), error.BlockIdHex);
    }

    [Fact]
    public void InsaneLength_RecordsOffsetAndNeverCarriesBlockId()
    {
        var error = CorruptionError.InsaneLength(offset: 100, declaredLength: 1_000_000_000, maxPayloadLength: 1024);

        Assert.Equal(CorruptionCause.InsaneLength, error.Cause);
        Assert.Equal(100, error.Offset);
        Assert.Null(error.BlockId);
        Assert.Equal("(none)", error.BlockIdHex);
        Assert.Contains("1000000000", error.Message);
        Assert.Contains("1024", error.Message);
    }

    [Fact]
    public void DecompressionBomb_IsCorruptionWithGuardInMessage()
    {
        var error = CorruptionError.DecompressionBomb(bombGuardBytes: 16384, offset: 2048);

        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
        Assert.Equal(CorruptionCause.DecompressionBomb, error.Cause);
        Assert.Equal(2048, error.Offset);
        Assert.Contains("16384", error.Message);
    }

    [Fact]
    public void GcmTagFailure_CarriesBlockIdOffsetEpochAndInner()
    {
        var id = SampleBlockId();
        var inner = new InvalidOperationException("tag mismatch");
        var error = WrongKeyOrTamperError.GcmTagFailure(id, offset: 5000, keyEpoch: 7, innerException: inner);

        Assert.Equal(TamperCause.GcmTag, error.Cause);
        Assert.Equal(5000, error.Offset);
        Assert.Equal(id, error.BlockId);
        Assert.Equal(7, error.KeyEpoch);
        Assert.Same(inner, error.InnerException);
    }

    [Fact]
    public void TokenMismatch_RecordsEpoch()
    {
        var error = WrongKeyOrTamperError.TokenMismatch(keyEpoch: 3);

        Assert.Equal(TamperCause.TokenMismatch, error.Cause);
        Assert.Equal(3, error.KeyEpoch);
        Assert.Null(error.BlockId);
        Assert.Null(error.Offset);
    }

    [Fact]
    public void MerkleChildHashMismatch_CapturesExpectedAndActualHashes()
    {
        var id = SampleBlockId();
        byte[] expected = [0xAA, 0xBB];
        byte[] actual = [0xCC, 0xDD];
        var error = IntegrityError.MerkleChildHashMismatch(id, offset: 9000, expected, actual);

        Assert.Equal(id, error.BlockId);
        Assert.Equal(9000, error.Offset);
        Assert.Equal(expected, error.ExpectedHash);
        Assert.Equal(actual, error.ActualHash);
    }

    // ------------------------------------------------------- Defensive copying

    [Fact]
    public void BlockId_IsDefensivelyCopied()
    {
        var id = SampleBlockId();
        var error = CorruptionError.ReferencedDataLoss(id, offset: 0);

        id[0] = 0xFF; // Mutate the caller's buffer after construction.

        Assert.NotEqual(0xFF, error.BlockId![0]);
        Assert.Equal(0x01, error.BlockId![0]);
    }

    [Fact]
    public void IntegrityHashes_AreDefensivelyCopied()
    {
        byte[] expected = [0x01, 0x02];
        var error = IntegrityError.MerkleChildHashMismatch(SampleBlockId(), expectedHash: expected);

        expected[0] = 0xFF;

        Assert.Equal(0x01, error.ExpectedHash![0]);
    }

    // -------------------------------------------------- Distinctness / catchability

    [Fact]
    public void EachError_IsCatchableAsItsBaseVerificationError()
    {
        VerificationError caught = Assert.Throws<CorruptionError>(
            void () => throw CorruptionError.PayloadChecksumMismatch(64));
        Assert.Equal(VerificationFailureKind.Corruption, caught.Kind);
    }

    [Fact]
    public void ThreeErrorTypes_AreMutuallyDistinctReferenceTypes()
    {
        VerificationError corruption = CorruptionError.PayloadChecksumMismatch(1);
        VerificationError tamper = WrongKeyOrTamperError.GcmTagFailure();
        VerificationError integrity = IntegrityError.MerkleChildHashMismatch();

        Assert.IsType<CorruptionError>(corruption);
        Assert.IsType<WrongKeyOrTamperError>(tamper);
        Assert.IsType<IntegrityError>(integrity);

        // The three Kinds are pairwise distinct — a switch on Kind is unambiguous.
        var kinds = new[] { corruption.Kind, tamper.Kind, integrity.Kind };
        Assert.Equal(3, kinds.Distinct().Count());
    }

    [Fact]
    public void CorruptionAndTamper_AreDistinguishableEvenForTheSameBlock()
    {
        // Verify order is checksum -> GCM tag: the same block can only ever raise one
        // of these, and the taxonomy keeps them apart (spec Section 13).
        var id = SampleBlockId();
        VerificationError corruption = CorruptionError.PayloadChecksumMismatch(100, id);
        VerificationError tamper = WrongKeyOrTamperError.GcmTagFailure(id, offset: 100);

        Assert.NotEqual(corruption.Kind, tamper.Kind);
        Assert.False(corruption is WrongKeyOrTamperError);
        Assert.False(tamper is CorruptionError);
    }

    // ---------------------------------------------------------- Result composition

    [Fact]
    public void ToResult_LowersIntoFailedGenericResultCarryingMessage()
    {
        var error = CorruptionError.HeaderChecksumMismatch(4096);
        Result<int> result = error.ToResult<int>();

        Assert.True(result.IsFailure);
        Assert.Equal(error.Message, result.Error);
    }

    [Fact]
    public void ToResult_LowersIntoFailedNonGenericResult()
    {
        var error = WrongKeyOrTamperError.TokenMismatch();
        Result result = error.ToResult();

        Assert.True(result.IsFailure);
        Assert.Equal(error.Message, result.Error);
    }
}
