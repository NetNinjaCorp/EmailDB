using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.V3;
using EmailDB.UnitTests.FaultInjection;

namespace EmailDB.UnitTests;

/// <summary>
/// End-to-end read-path taxonomy for policy-driven encryption (US-EMDB-78-6, spec
/// Section 9.5 / Section 13, docs/Encryption.md Section 3): drives real encrypted blocks
/// through <see cref="EncryptedBlockStore.ReadDecrypted"/> and asserts the on-disk verify
/// order and the three distinct crypto error classes.
///
/// <para>The load-bearing guarantees:</para>
/// <list type="number">
///   <item><b>Checksum before GCM tag.</b> The PayloadChecksum is computed over the
///   on-disk <i>ciphertext</i> and verified FIRST. A flipped ciphertext byte therefore
///   surfaces as <see cref="CorruptionError"/> (<see cref="CorruptionCause.PayloadChecksum"/>) —
///   never as a GCM/authentication error. The cipher is not even reached.</item>
///   <item><b>Checksum-valid but tag fails =&gt; wrong-key-or-tamper.</b> When the
///   ciphertext is tampered AND its checksum repaired (or read under the wrong DEK), the
///   checksum passes and the GCM tag fails: a distinct <see cref="WrongKeyOrTamperError"/>
///   (<see cref="TamperCause.GcmTag"/>) carrying the header's KeyEpoch — the spec's "wrong
///   key or tampering, not corruption" class. Wrong-key and tamper are the SAME class by
///   design (spec Section 13 groups them).</item>
///   <item><b>Missing/retired epoch =&gt; its own class.</b> A block whose header KeyEpoch
///   has no live DEK surfaces <see cref="EpochDekUnavailableError"/> — the cipher never
///   runs; the reader never brute-forces other epochs.</item>
///   <item><b>Plaintext blocks bypass decrypt.</b> A block with the Encrypted flag clear is
///   returned verbatim without touching the DEK, so a mixed-policy file reads correctly
///   block-by-block off the flag even when the encrypted blocks would fail to decrypt.</item>
/// </list>
/// </summary>
public class ReadPathDecryptTaxonomyTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p)) File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xB0 + i)).ToArray();

    private const ushort ActiveEpoch = 4;

    private string NewPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"emaildb-readtax-{Guid.NewGuid():N}.emdb");
        _paths.Add(p);
        return p;
    }

    private static FileStream OpenRW(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static byte[] Dek(byte seed)
    {
        var dek = new byte[AesGcmBlockCipher.KeySize];
        for (var i = 0; i < dek.Length; i++) dek[i] = (byte)(i + seed);
        return dek;
    }

    /// <summary>A provider for <paramref name="activeEpoch"/> with a DEK derived from <paramref name="dekSeed"/>.</summary>
    private static EpochDekProvider Provider(ushort activeEpoch = ActiveEpoch, byte dekSeed = 1) =>
        new(FileId, activeEpoch, new[] { new EpochDekProvider.EpochDek(activeEpoch, Dek(dekSeed)) });

    /// <summary>
    /// Writes a single encrypted block under <see cref="ActiveEpoch"/> (DEK seed 1) and
    /// closes the file so the ciphertext is cold on disk. Returns its offset and the on-disk
    /// (encrypted) payload length.
    /// </summary>
    private (string Path, long Offset, int PayloadLength) WriteEncryptedBlock(
        byte[] plaintext, BlockType type = BlockType.EmailContent)
    {
        var path = NewPath();
        long offset;
        int payloadLength;
        using (var provider = Provider())
        using (var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true))
        {
            var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
            var appended = store.Append(type, PayloadEncoding.RawBytes, plaintext);
            Ok(appended);
            offset = appended.Value.Offset;

            var raw = manager.Read(offset);
            Ok(raw);
            Assert.True(raw.Value.Header.IsEncrypted);
            Assert.Equal(ActiveEpoch, raw.Value.Header.KeyEpoch);
            payloadLength = raw.Value.Payload.Length; // nonce + ciphertext + tag

            Ok(manager.Flush());
        }
        return (path, offset, payloadLength);
    }

    /// <summary>
    /// Flips one on-disk ciphertext byte AND recomputes the PayloadChecksum over the
    /// tampered bytes, so the block passes the checksum yet fails GCM authentication —
    /// isolating the "checksum-valid, tag-fails" case (spec Section 13).
    /// </summary>
    private static void TamperCiphertextRepairingChecksum(string path, long blockOffset, int payloadLength, int payloadByteIndex)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long payloadStart = blockOffset + BlockSerializer.PayloadOffset;

        fs.Seek(payloadStart + payloadByteIndex, SeekOrigin.Begin);
        int original = fs.ReadByte();
        fs.Seek(payloadStart + payloadByteIndex, SeekOrigin.Begin);
        fs.WriteByte((byte)(original ^ 0xFF));

        var payload = new byte[payloadLength];
        fs.Seek(payloadStart, SeekOrigin.Begin);
        fs.ReadExactly(payload, 0, payloadLength);

        var checksum = new byte[BlockSerializer.ChecksumSize];
        BlockSerializer.ComputePayloadChecksum(payload, checksum);
        fs.Seek(payloadStart + payloadLength, SeekOrigin.Begin);
        fs.Write(checksum, 0, checksum.Length);
        fs.Flush(flushToDisk: true);
    }

    // ---- (1) Checksum verified before the GCM tag ---------------------------------

    [Fact]
    public void CorruptCiphertextByte_SurfacesCorruption_NeverAGcmError()
    {
        var (path, offset, _) = WriteEncryptedBlock("secret payload for checksum-first proof"u8.ToArray());

        // Flip a ciphertext byte WITHOUT repairing the checksum: the payload checksum
        // (over ciphertext) is the first gate and must catch this as corruption.
        new FaultInjector(path).FlipPayloadByte(offset, payloadByteIndex: 20);

        using var provider = Provider();
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        var read = store.ReadDecrypted(offset);
        Assert.True(read.IsFailure);

        // Corruption, not a GCM/authentication error: the cipher was never reached.
        var error = Assert.IsType<CorruptionError>(read.VerificationError);
        Assert.Equal(VerificationFailureKind.Corruption, error.Kind);
        Assert.Equal(CorruptionCause.PayloadChecksum, error.Cause);
        Assert.False(read.VerificationError is WrongKeyOrTamperError);
    }

    // ---- (2) Checksum valid, GCM tag fails => wrong-key-or-tamper -----------------

    [Fact]
    public void TamperedCiphertext_WithRepairedChecksum_SurfacesWrongKeyOrTamper()
    {
        var (path, offset, payloadLength) =
            WriteEncryptedBlock("tamper me, then fix my checksum"u8.ToArray());

        // Tamper a ciphertext byte and repair the checksum so the FIRST gate passes and the
        // GCM tag becomes the failing check: the "checksum-valid, tag-fails" boundary.
        TamperCiphertextRepairingChecksum(path, offset, payloadLength, payloadByteIndex: 18);

        using var provider = Provider();
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);

        // The checksum genuinely passes now — manager.Read succeeds.
        Ok(manager.Read(offset));

        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
        var error = Assert.Throws<WrongKeyOrTamperError>(() => store.ReadDecrypted(offset));
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, error.Kind);
        Assert.Equal(TamperCause.GcmTag, error.Cause);
        Assert.Equal(ActiveEpoch, error.KeyEpoch); // attempted under the header epoch only
    }

    [Fact]
    public void WrongKey_SameEpochDifferentDek_SurfacesWrongKeyOrTamper()
    {
        var (path, offset, _) = WriteEncryptedBlock("body encrypted under the true DEK"u8.ToArray());

        // On-disk bytes are the legitimately written ciphertext, so the checksum passes.
        // Reading under the WRONG DEK (same epoch) fails GCM authentication: wrong key.
        using var wrongKey = Provider(dekSeed: 99);
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);
        Ok(manager.Read(offset)); // checksum passes: bytes untouched

        var store = new EncryptedBlockStore(manager, wrongKey, EncryptionPolicy.Default);
        var error = Assert.Throws<WrongKeyOrTamperError>(() => store.ReadDecrypted(offset));
        Assert.Equal(TamperCause.GcmTag, error.Cause);
        Assert.Equal(ActiveEpoch, error.KeyEpoch); // never brute-forced another epoch
    }

    // ---- (3) Missing / retired epoch is its own distinct class --------------------

    [Fact]
    public void MissingEpoch_SurfacesEpochDekUnavailable_Missing()
    {
        var (path, offset, _) = WriteEncryptedBlock("stamped at epoch 4"u8.ToArray());

        // A provider active at a different epoch with NO entry for the block's epoch 4.
        using var provider = new EpochDekProvider(
            FileId, activeEpoch: 7, new[] { new EpochDekProvider.EpochDek(7, Dek(2)) });
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        var error = Assert.Throws<EpochDekUnavailableError>(() => store.ReadDecrypted(offset));
        Assert.Equal(EpochDekUnavailableReason.Missing, error.Reason);
        Assert.Equal(ActiveEpoch, error.KeyEpoch);
    }

    [Fact]
    public void RetiredEpoch_SurfacesEpochDekUnavailable_Retired()
    {
        var (path, offset, _) = WriteEncryptedBlock("stamped at a now-retired epoch"u8.ToArray());

        // Epoch 4's DEK is retired (pruned); a live active epoch 7 exists for the provider.
        using var provider = new EpochDekProvider(FileId, activeEpoch: 7, new[]
        {
            new EpochDekProvider.EpochDek(7, Dek(2)),
            new EpochDekProvider.EpochDek(ActiveEpoch, ReadOnlyMemory<byte>.Empty, retired: true),
        });
        using var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);
        var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);

        var error = Assert.Throws<EpochDekUnavailableError>(() => store.ReadDecrypted(offset));
        Assert.Equal(EpochDekUnavailableReason.Retired, error.Reason);
        Assert.Equal(ActiveEpoch, error.KeyEpoch);
    }

    // ---- The three error classes are distinct types -------------------------------

    [Fact]
    public void CorruptionWrongKeyAndEpochUnavailable_AreThreeDistinctTypes()
    {
        // (a) Corruption: flipped ciphertext, checksum not repaired.
        var corruptFile = WriteEncryptedBlock("distinct-a"u8.ToArray());
        new FaultInjector(corruptFile.Path).FlipPayloadByte(corruptFile.Offset, payloadByteIndex: 15);
        Exception corruption;
        using (var provider = Provider())
        using (var manager = new BlockManager(OpenRW(corruptFile.Path), firstBlockOffset: 0, ownsStream: true))
        {
            var read = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default).ReadDecrypted(corruptFile.Offset);
            corruption = read.VerificationError!;
        }

        // (b) Wrong key / tamper: read intact ciphertext under a different DEK.
        var wrongKeyFile = WriteEncryptedBlock("distinct-b"u8.ToArray());
        Exception wrongKey;
        using (var provider = Provider(dekSeed: 77))
        using (var manager = new BlockManager(OpenRW(wrongKeyFile.Path), firstBlockOffset: 0, ownsStream: true))
        {
            var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
            wrongKey = Assert.Throws<WrongKeyOrTamperError>(() => store.ReadDecrypted(wrongKeyFile.Offset));
        }

        // (c) Epoch unavailable: no DEK for the header epoch.
        var missingFile = WriteEncryptedBlock("distinct-c"u8.ToArray());
        Exception epochGone;
        using (var provider = new EpochDekProvider(FileId, 7, new[] { new EpochDekProvider.EpochDek(7, Dek(2)) }))
        using (var manager = new BlockManager(OpenRW(missingFile.Path), firstBlockOffset: 0, ownsStream: true))
        {
            var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
            epochGone = Assert.Throws<EpochDekUnavailableError>(() => store.ReadDecrypted(missingFile.Offset));
        }

        // Three distinct concrete types, none assignable to another.
        Assert.IsType<CorruptionError>(corruption);
        Assert.IsType<WrongKeyOrTamperError>(wrongKey);
        Assert.IsType<EpochDekUnavailableError>(epochGone);

        Assert.False(corruption is WrongKeyOrTamperError);
        Assert.False(corruption is EpochDekUnavailableError);
        Assert.False(wrongKey is CorruptionError);
        Assert.False(wrongKey is EpochDekUnavailableError);
        Assert.False(epochGone is VerificationError); // its own crypto-domain class
        Assert.IsAssignableFrom<CryptographicException>(epochGone);

        Assert.Equal(3, new[] { corruption.GetType(), wrongKey.GetType(), epochGone.GetType() }.Distinct().Count());
    }

    // ---- (4) Mixed-policy files read correctly block-by-block via the Encrypted flag ----

    [Fact]
    public void MixedPolicyFile_PlaintextBypassesDecrypt_EncryptedRoutesViaFlag()
    {
        var content = "encrypted email body"u8.ToArray();       // EmailContent -> encrypted (Default)
        var btree = "plaintext btree keys"u8.ToArray();          // BTreeLeaf    -> plaintext  (Default)
        var path = NewPath();
        long contentOffset, btreeOffset;

        using (var provider = Provider())
        using (var manager = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true))
        {
            var store = new EncryptedBlockStore(manager, provider, EncryptionPolicy.Default);
            var a = store.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, content);
            var b = store.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, btree);
            Ok(a); Ok(b);
            contentOffset = a.Value.Offset; btreeOffset = b.Value.Offset;

            Assert.True(manager.Read(contentOffset).Value.Header.IsEncrypted);
            Assert.False(manager.Read(btreeOffset).Value.Header.IsEncrypted);
            Ok(manager.Flush());
        }

        // Reopen with the WRONG DEK. The plaintext block bypasses decryption (flag clear),
        // so it reads correctly even though the DEK cannot open the encrypted block — proving
        // the read path routes per-block off the Encrypted flag, not the file as a whole.
        using var wrongKey = Provider(dekSeed: 55);
        using var reader = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);
        var readStore = new EncryptedBlockStore(reader, wrongKey, EncryptionPolicy.Default);

        var plain = readStore.ReadDecrypted(btreeOffset);
        Ok(plain);
        Assert.Equal(btree, plain.Value);                                    // plaintext bypasses the DEK

        Assert.Throws<WrongKeyOrTamperError>(() => readStore.ReadDecrypted(contentOffset)); // encrypted still needs it

        // With the RIGHT DEK, both blocks read back correctly block-by-block.
        using var right = Provider();
        using var reader2 = new BlockManager(OpenRW(path), firstBlockOffset: 0, ownsStream: true);
        var rightStore = new EncryptedBlockStore(reader2, right, EncryptionPolicy.Default);
        Assert.Equal(content, rightStore.ReadDecrypted(contentOffset).Value);
        Assert.Equal(btree, rightStore.ReadDecrypted(btreeOffset).Value);
    }
}
