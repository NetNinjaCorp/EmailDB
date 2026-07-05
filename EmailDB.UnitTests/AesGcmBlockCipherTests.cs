using System.Reflection;
using System.Security.Cryptography;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Unit tests for the v3 per-block AES-256-GCM primitive
/// (<see cref="AesGcmBlockCipher"/>, spec Section 9.3): random-nonce generation,
/// mandatory AAD binding (FileId|BlockId|BlockType|KeyEpoch), and the
/// Nonce|Ciphertext|Tag on-disk layout adding exactly 28 bytes.
/// </summary>
public class AesGcmBlockCipherTests
{
    private static byte[] Dek() => RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
    private static byte[] Id() => RandomNumberGenerator.GetBytes(AesGcmBlockCipher.IdSize);

    [Fact]
    public void OnDiskLayout_AddsExactly28Bytes_NonceThenCiphertextThenTag()
    {
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();
        var plaintext = RandomNumberGenerator.GetBytes(137);

        var payload = AesGcmBlockCipher.Encrypt(
            plaintext, dek, fileId, blockId, BlockType.EmailContent, keyEpoch: 3);

        Assert.Equal(28, AesGcmBlockCipher.Overhead);
        Assert.Equal(plaintext.Length + 28, payload.Length);
        // Nonce occupies the first 12 bytes; ciphertext differs from plaintext.
        Assert.False(payload.AsSpan(12, plaintext.Length).SequenceEqual(plaintext));
    }

    [Fact]
    public void OnDiskLayout_IsPositionally_NonceThenCiphertextThenTag_ProvenByManualReassembly()
    {
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();
        const BlockType type = BlockType.EmailContent;
        const ushort epoch = 9;
        var plaintext = RandomNumberGenerator.GetBytes(100);

        var payload = AesGcmBlockCipher.Encrypt(plaintext, dek, fileId, blockId, type, epoch);

        // Carve the three positional segments straight out of the on-disk layout:
        // nonce = the first 12 bytes, ciphertext = the middle, tag = the last 16 bytes.
        var nonce = payload.AsSpan(0, AesGcmBlockCipher.NonceSize).ToArray();
        var ciphertext = payload
            .AsSpan(AesGcmBlockCipher.NonceSize, payload.Length - AesGcmBlockCipher.Overhead).ToArray();
        var tag = payload
            .AsSpan(payload.Length - AesGcmBlockCipher.TagSize, AesGcmBlockCipher.TagSize).ToArray();
        Assert.Equal(plaintext.Length, ciphertext.Length);

        // Independently of AesGcmBlockCipher.Decrypt, feed exactly those carved segments to a
        // raw AES-GCM. It only recovers the plaintext if each segment truly occupies the byte
        // range claimed by the layout — proving nonce first, ciphertext middle, tag last.
        Span<byte> aad = stackalloc byte[AesGcmBlockCipher.AadSize];
        AesGcmBlockCipher.BuildAad(fileId, blockId, type, epoch, aad);
        var recovered = new byte[ciphertext.Length];
        using (var aes = new AesGcm(dek, AesGcmBlockCipher.TagSize))
            aes.Decrypt(nonce, ciphertext, tag, recovered, aad);
        Assert.Equal(plaintext, recovered);

        // Order is load-bearing: reassembling the same bytes as Tag|Ciphertext|Nonce must fail
        // to authenticate, so the three regions are positional and not interchangeable.
        var swapped = new byte[payload.Length];
        tag.CopyTo(swapped, 0);
        ciphertext.CopyTo(swapped, AesGcmBlockCipher.TagSize);
        nonce.CopyTo(swapped, AesGcmBlockCipher.TagSize + ciphertext.Length);
        Assert.Throws<WrongKeyOrTamperError>(() =>
            AesGcmBlockCipher.Decrypt(swapped, dek, fileId, blockId, type, epoch));
    }

    [Fact]
    public void RoundTrip_WithMatchingAad_RecoversPlaintext()
    {
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();
        var plaintext = RandomNumberGenerator.GetBytes(64);

        var payload = AesGcmBlockCipher.Encrypt(
            plaintext, dek, fileId, blockId, BlockType.EmailMetadata, keyEpoch: 7);
        var decrypted = AesGcmBlockCipher.Decrypt(
            payload, dek, fileId, blockId, BlockType.EmailMetadata, keyEpoch: 7);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EmptyPlaintext_RoundTrips_And_HasOnly28Bytes()
    {
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();

        var payload = AesGcmBlockCipher.Encrypt(
            ReadOnlySpan<byte>.Empty, dek, fileId, blockId, BlockType.WAL, keyEpoch: 0);

        Assert.Equal(28, payload.Length);
        var decrypted = AesGcmBlockCipher.Decrypt(
            payload, dek, fileId, blockId, BlockType.WAL, keyEpoch: 0);
        Assert.Empty(decrypted);
    }

    [Fact]
    public void Nonce_IsRandom_NotDerivedFromIds_SameInputsGiveDifferentNonces()
    {
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();
        var plaintext = RandomNumberGenerator.GetBytes(48);

        var a = AesGcmBlockCipher.Encrypt(plaintext, dek, fileId, blockId, BlockType.EmailContent, 1);
        var b = AesGcmBlockCipher.Encrypt(plaintext, dek, fileId, blockId, BlockType.EmailContent, 1);

        var nonceA = a.AsSpan(0, 12);
        var nonceB = b.AsSpan(0, 12);
        // Identical inputs (same IDs, epoch, DEK, plaintext) must still yield distinct
        // nonces — proving the nonce is CSPRNG-random, not derived from the IDs.
        Assert.False(nonceA.SequenceEqual(nonceB));
        // And nonce bytes are not simply the BlockId prefix.
        Assert.False(nonceA.SequenceEqual(blockId.AsSpan(0, 12)));
    }

    [Fact]
    public void Nonce_IsExactly12Bytes_CsprngSized()
    {
        // The nonce region is exactly NonceSize (12) bytes and NonceSize is the
        // GCM-standard CSPRNG nonce length; overhead is nonce(12)+tag(16).
        Assert.Equal(12, AesGcmBlockCipher.NonceSize);
        Assert.Equal(AesGcmBlockCipher.NonceSize + AesGcmBlockCipher.TagSize, AesGcmBlockCipher.Overhead);

        var payload = AesGcmBlockCipher.Encrypt(
            RandomNumberGenerator.GetBytes(50), Dek(), Id(), Id(), BlockType.EmailContent, keyEpoch: 4);

        // Ciphertext length is payload minus exactly the 12-byte nonce and 16-byte tag,
        // so the leading nonce field is precisely 12 bytes wide.
        Assert.Equal(50, payload.Length - AesGcmBlockCipher.NonceSize - AesGcmBlockCipher.TagSize);
    }

    [Fact]
    public void Nonce_IsCsprng_NotACounter_AcrossManyEncrypts()
    {
        // A monotonic counter and a CSPRNG both yield distinct nonces per call, so
        // uniqueness alone cannot tell them apart. Encrypt many times with *identical*
        // inputs and prove the nonce stream has the statistical shape of a CSPRNG, not
        // a counter or an ID-derived value.
        const int n = 512;
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();
        var plaintext = RandomNumberGenerator.GetBytes(16);

        var nonces = new List<byte[]>(n);
        for (var i = 0; i < n; i++)
        {
            var payload = AesGcmBlockCipher.Encrypt(plaintext, dek, fileId, blockId, BlockType.EmailContent, 1);
            nonces.Add(payload.AsSpan(0, AesGcmBlockCipher.NonceSize).ToArray());
        }

        // Every nonce is exactly 12 bytes and all are distinct (no collisions in 512 draws).
        Assert.All(nonces, nz => Assert.Equal(12, nz.Length));
        Assert.Equal(n, nonces.Select(Convert.ToHexString).Distinct().Count());

        // Not a counter: a counter emits nonces in strictly increasing (sorted) order.
        // A CSPRNG stream is overwhelmingly unlikely to arrive already sorted.
        var sorted = nonces.OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToList();
        var inGenerationOrder = nonces
            .Select(Convert.ToHexString)
            .SequenceEqual(sorted.Select(Convert.ToHexString));
        Assert.False(inGenerationOrder, "Nonces arrived in sorted order — looks like a counter, not CSPRNG.");

        // Full-width randomness: every one of the 12 byte positions varies across the
        // samples (no fixed/derived bytes anywhere, e.g. a zero-padded counter would
        // leave high-order bytes constant).
        for (var pos = 0; pos < AesGcmBlockCipher.NonceSize; pos++)
        {
            var distinctAtPos = nonces.Select(nz => nz[pos]).Distinct().Count();
            Assert.True(distinctAtPos > 1, $"Nonce byte position {pos} never varied across {n} nonces.");
        }

        // Not derived from the (fixed) inputs: no nonce equals the BlockId or FileId prefix.
        Assert.DoesNotContain(nonces, nz => nz.AsSpan().SequenceEqual(blockId.AsSpan(0, 12)));
        Assert.DoesNotContain(nonces, nz => nz.AsSpan().SequenceEqual(fileId.AsSpan(0, 12)));
    }

    [Theory]
    [InlineData("blockId")]
    [InlineData("blockType")]
    [InlineData("epoch")]
    [InlineData("fileId")]
    public void CiphertextMovedToDifferentIdentity_FailsAuthentication(string moved)
    {
        var dek = Dek();
        var fileId = Id();
        var blockId = Id();
        const BlockType type = BlockType.EmailContent;
        const ushort epoch = 5;
        var plaintext = RandomNumberGenerator.GetBytes(80);

        var payload = AesGcmBlockCipher.Encrypt(plaintext, dek, fileId, blockId, type, epoch);

        // Rebind exactly one AAD component to a different value.
        var decFileId = fileId;
        var decBlockId = blockId;
        var decType = type;
        var decEpoch = epoch;
        switch (moved)
        {
            case "blockId": decBlockId = Id(); break;
            case "blockType": decType = BlockType.EmailMetadata; break;
            case "epoch": decEpoch = epoch + 1; break;
            case "fileId": decFileId = Id(); break;
        }

        var ex = Assert.Throws<WrongKeyOrTamperError>(() =>
            AesGcmBlockCipher.Decrypt(payload, dek, decFileId, decBlockId, decType, decEpoch));
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
        Assert.Equal(TamperCause.GcmTag, ex.Cause);
    }

    [Fact]
    public void EncryptAndDecrypt_StructurallyRequireAllFourAadComponents()
    {
        // "Both operations require AAD" is guaranteed structurally: every public Encrypt and
        // Decrypt entry point takes fileId, blockId, blockType and keyEpoch, and there is no
        // overload that omits any of them — it is impossible to call either without the AAD.
        string[] aad = { "fileId", "blockId", "blockType", "keyEpoch" };
        foreach (var name in new[] { nameof(AesGcmBlockCipher.Encrypt), nameof(AesGcmBlockCipher.Decrypt) })
        {
            var overloads = typeof(AesGcmBlockCipher)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name)
                .ToList();
            Assert.NotEmpty(overloads);
            Assert.All(overloads, m =>
            {
                var pnames = m.GetParameters().Select(p => p.Name).ToHashSet();
                foreach (var component in aad)
                    Assert.Contains(component, pnames);
            });
        }
    }

    [Fact]
    public void WrongKey_FailsAuthentication_AsWrongKeyOrTamper()
    {
        var fileId = Id();
        var blockId = Id();
        var plaintext = RandomNumberGenerator.GetBytes(32);

        var payload = AesGcmBlockCipher.Encrypt(plaintext, Dek(), fileId, blockId, BlockType.EmailContent, 2);

        Assert.Throws<WrongKeyOrTamperError>(() =>
            AesGcmBlockCipher.Decrypt(payload, Dek(), fileId, blockId, BlockType.EmailContent, 2));
    }

    [Fact]
    public void BuildAad_ProducesSpecLayout_35Bytes_KeyEpochLittleEndian()
    {
        Assert.Equal(35, AesGcmBlockCipher.AadSize);

        var fileId = RandomNumberGenerator.GetBytes(16);
        var blockId = RandomNumberGenerator.GetBytes(16);
        Span<byte> aad = stackalloc byte[AesGcmBlockCipher.AadSize];
        AesGcmBlockCipher.BuildAad(fileId, blockId, BlockType.BloomFilter, keyEpoch: 0x0102, aad);

        Assert.True(aad[..16].SequenceEqual(fileId));
        Assert.True(aad.Slice(16, 16).SequenceEqual(blockId));
        Assert.Equal((byte)BlockType.BloomFilter, aad[32]);
        Assert.Equal(0x02, aad[33]); // low byte first (little-endian)
        Assert.Equal(0x01, aad[34]);
    }

    [Fact]
    public void Decrypt_TooShortPayload_Throws()
    {
        var dek = Dek();
        Assert.Throws<ArgumentException>(() =>
            AesGcmBlockCipher.Decrypt(new byte[27], dek, Id(), Id(), BlockType.WAL, 0));
    }
}
