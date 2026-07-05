using System.Reflection;
using System.Security.Cryptography;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Unit tests for the v3 epoch-aware provider (<see cref="EpochDekProvider"/>,
/// docs/Encryption.md Sections 1-3, task US-EMDB-77-6): encrypt under the active epoch,
/// decrypt by selecting the DEK from the block header <c>KeyEpoch</c>, distinct errors for
/// a missing vs a retired epoch, and zeroization of DEK/KEK buffers on disposal.
/// </summary>
public class EpochDekProviderTests
{
    private static byte[] Dek() => RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
    private static byte[] Id() => RandomNumberGenerator.GetBytes(AesGcmBlockCipher.IdSize);

    private static EpochDekProvider.EpochDek Live(ushort epoch, byte[] dek) => new(epoch, dek, retired: false);
    private static EpochDekProvider.EpochDek Retired(ushort epoch) => new(epoch, ReadOnlyMemory<byte>.Empty, retired: true);

    // --- Encrypt/decrypt round trip and epoch-selected decrypt ---

    [Fact]
    public void Encrypt_UsesActiveEpoch_RoundTripsWithHeaderEpoch()
    {
        var fileId = Id();
        var blockId = Id();
        using var provider = new EpochDekProvider(fileId, activeEpoch: 2,
            new[] { Live(1, Dek()), Live(2, Dek()) });

        var plaintext = "active epoch payload"u8.ToArray();
        var payload = provider.Encrypt(plaintext, blockId, BlockType.EmailContent);

        // The block header records ActiveEpoch; decrypt selects the DEK by that epoch.
        var decrypted = provider.Decrypt(payload, blockId, BlockType.EmailContent, provider.ActiveEpoch);
        Assert.Equal((ushort)2, provider.ActiveEpoch);
        Assert.Equal(plaintext, decrypted);
        Assert.Equal(plaintext.Length + 28, payload.Length);
    }

    [Fact]
    public void Encrypt_OnDiskLayout_IsPositionally_NonceThenCiphertextThenTag()
    {
        var fileId = Id();
        var blockId = Id();
        const BlockType type = BlockType.EmailContent;
        var dek2 = Dek();
        using var provider = new EpochDekProvider(fileId, activeEpoch: 2,
            new[] { Live(1, Dek()), Live(2, dek2) });

        var plaintext = "provider on-disk layout"u8.ToArray();
        var payload = provider.Encrypt(plaintext, blockId, type);

        // Same +28 overhead as the primitive: nonce(12) + tag(16).
        Assert.Equal(plaintext.Length + 28, payload.Length);
        Assert.Equal(AesGcmBlockCipher.Overhead, payload.Length - plaintext.Length);

        // Carve the positional segments out of the provider's output.
        var nonce = payload.AsSpan(0, AesGcmBlockCipher.NonceSize).ToArray();
        var ciphertext = payload
            .AsSpan(AesGcmBlockCipher.NonceSize, payload.Length - AesGcmBlockCipher.Overhead).ToArray();
        var tag = payload
            .AsSpan(payload.Length - AesGcmBlockCipher.TagSize, AesGcmBlockCipher.TagSize).ToArray();
        Assert.Equal(plaintext.Length, ciphertext.Length);

        // The provider stamps ActiveEpoch (2) into the AAD; a raw AES-GCM fed exactly these
        // segments recovers the plaintext only if the provider emits the identical
        // Nonce|Ciphertext|Tag layout as the primitive.
        Span<byte> aad = stackalloc byte[AesGcmBlockCipher.AadSize];
        AesGcmBlockCipher.BuildAad(fileId, blockId, type, provider.ActiveEpoch, aad);
        var recovered = new byte[ciphertext.Length];
        using (var aes = new AesGcm(dek2, AesGcmBlockCipher.TagSize))
            aes.Decrypt(nonce, ciphertext, tag, recovered, aad);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void Encrypt_EmptyPayload_IsExactly28Bytes()
    {
        var fileId = Id();
        var blockId = Id();
        using var provider = new EpochDekProvider(fileId, activeEpoch: 0, new[] { Live(0, Dek()) });

        var payload = provider.Encrypt(ReadOnlySpan<byte>.Empty, blockId, BlockType.WAL);

        // Empty plaintext collapses the layout to just Nonce(12) + Tag(16) = exactly 28 bytes.
        Assert.Equal(28, payload.Length);
        Assert.Equal(AesGcmBlockCipher.Overhead, payload.Length);
        Assert.Empty(provider.Decrypt(payload, blockId, BlockType.WAL, provider.ActiveEpoch));
    }

    [Fact]
    public void Decrypt_SelectsDekByHeaderKeyEpoch_AcrossEpochs()
    {
        var fileId = Id();
        var dek0 = Dek();
        var dek1 = Dek();
        // Build two ciphertexts, each sealed under a different epoch, using two providers
        // that share the same DEK table but differ only in active epoch.
        var blockOld = Id();
        var blockNew = Id();

        byte[] ctOld, ctNew;
        using (var p0 = new EpochDekProvider(fileId, activeEpoch: 0, new[] { Live(0, dek0), Live(1, dek1) }))
            ctOld = p0.Encrypt("old epoch"u8.ToArray(), blockOld, BlockType.EmailMetadata);
        using (var p1 = new EpochDekProvider(fileId, activeEpoch: 1, new[] { Live(0, dek0), Live(1, dek1) }))
            ctNew = p1.Encrypt("new epoch"u8.ToArray(), blockNew, BlockType.EmailMetadata);

        using var reader = new EpochDekProvider(fileId, activeEpoch: 1, new[] { Live(0, dek0), Live(1, dek1) });
        // Reader picks the right DEK purely from the header epoch, not the active epoch.
        Assert.Equal("old epoch"u8.ToArray(), reader.Decrypt(ctOld, blockOld, BlockType.EmailMetadata, keyEpoch: 0));
        Assert.Equal("new epoch"u8.ToArray(), reader.Decrypt(ctNew, blockNew, BlockType.EmailMetadata, keyEpoch: 1));
    }

    [Fact]
    public void Encrypt_ProducesCsprng12ByteNonce_NotDerivedFromBlockIdOrCounter()
    {
        // The provider layer must also emit 12-byte CSPRNG nonces: encrypting the same
        // plaintext under the same blockId/active epoch repeatedly yields distinct nonces
        // that are never derived from the BlockId and never a monotonic counter.
        const int n = 256;
        var fileId = Id();
        var blockId = Id();
        using var provider = new EpochDekProvider(fileId, activeEpoch: 3,
            new[] { Live(0, Dek()), Live(3, Dek()) });

        var plaintext = "same input every time"u8.ToArray();
        var nonces = new List<byte[]>(n);
        for (var i = 0; i < n; i++)
        {
            var payload = provider.Encrypt(plaintext, blockId, BlockType.EmailContent);
            nonces.Add(payload.AsSpan(0, AesGcmBlockCipher.NonceSize).ToArray());
        }

        // Exactly 12 bytes, all distinct across identical inputs.
        Assert.All(nonces, nz => Assert.Equal(12, nz.Length));
        Assert.Equal(n, nonces.Select(Convert.ToHexString).Distinct().Count());

        // Never derived from the BlockId prefix.
        Assert.DoesNotContain(nonces, nz => nz.AsSpan().SequenceEqual(blockId.AsSpan(0, 12)));

        // Not a counter: the CSPRNG stream does not arrive in sorted (monotonic) order.
        var sorted = nonces.OrderBy(Convert.ToHexString, StringComparer.Ordinal)
            .Select(Convert.ToHexString);
        Assert.False(nonces.Select(Convert.ToHexString).SequenceEqual(sorted),
            "Provider nonces arrived sorted — looks like a counter, not CSPRNG.");
    }

    // --- Missing / retired epoch: distinct errors ---

    [Fact]
    public void Decrypt_MissingEpoch_ThrowsEpochDekUnavailable_MissingReason()
    {
        var fileId = Id();
        var blockId = Id();
        using var provider = new EpochDekProvider(fileId, activeEpoch: 0, new[] { Live(0, Dek()) });
        var ct = provider.Encrypt("data"u8.ToArray(), blockId, BlockType.EmailContent);

        var ex = Assert.Throws<EpochDekUnavailableError>(() =>
            provider.Decrypt(ct, blockId, BlockType.EmailContent, keyEpoch: 42));

        Assert.Equal(EpochDekUnavailableReason.Missing, ex.Reason);
        Assert.Equal(42, ex.KeyEpoch);
        Assert.Contains("42", ex.Message);
        // Crypto-domain error, never a bare dictionary miss.
        Assert.IsAssignableFrom<CryptographicException>(ex);
        Assert.IsNotType<KeyNotFoundException>(ex);
    }

    [Fact]
    public void Decrypt_RetiredEpoch_ThrowsEpochDekUnavailable_RetiredReason()
    {
        var fileId = Id();
        var blockId = Id();
        // Epoch 0 retired (DEK pruned), epoch 1 live and active.
        using var provider = new EpochDekProvider(fileId, activeEpoch: 1, new[] { Retired(0), Live(1, Dek()) });
        var ct = provider.Encrypt("data"u8.ToArray(), blockId, BlockType.EmailContent);

        var ex = Assert.Throws<EpochDekUnavailableError>(() =>
            provider.Decrypt(ct, blockId, BlockType.EmailContent, keyEpoch: 0));

        Assert.Equal(EpochDekUnavailableReason.Retired, ex.Reason);
        Assert.Equal(0, ex.KeyEpoch);
    }

    [Fact]
    public void MissingAndRetired_AreDistinguishable()
    {
        var fileId = Id();
        var blockId = Id();
        using var provider = new EpochDekProvider(fileId, activeEpoch: 1, new[] { Retired(0), Live(1, Dek()) });
        var ct = provider.Encrypt("data"u8.ToArray(), blockId, BlockType.EmailContent);

        var missing = Assert.Throws<EpochDekUnavailableError>(() =>
            provider.Decrypt(ct, blockId, BlockType.EmailContent, keyEpoch: 5)).Reason;
        var retired = Assert.Throws<EpochDekUnavailableError>(() =>
            provider.Decrypt(ct, blockId, BlockType.EmailContent, keyEpoch: 0)).Reason;

        Assert.Equal(EpochDekUnavailableReason.Missing, missing);
        Assert.Equal(EpochDekUnavailableReason.Retired, retired);
        Assert.NotEqual(missing, retired);
    }

    // --- AAD binding at the provider layer: a ciphertext moved to a different identity
    //     fails authentication for every one of the four AAD components. FileId is bound to
    //     the provider instance, so its transplant is modelled by decrypting through a second
    //     provider that differs only in FileId while sharing the same DEK table. ---

    [Theory]
    [InlineData("blockId")]
    [InlineData("blockType")]
    [InlineData("epoch")]
    [InlineData("fileId")]
    public void Decrypt_CiphertextMovedToDifferentIdentity_FailsAuthentication(string moved)
    {
        var fileId = Id();
        var blockId = Id();
        const BlockType type = BlockType.EmailContent;
        const ushort epoch = 1;
        var dek0 = Dek();
        var dek1 = Dek();

        // Shared DEK table with a live key for every epoch, so an authentication failure can
        // never be confused with an unavailable-epoch error (the cipher does run).
        EpochDekProvider.EpochDek[] Table() => new[] { Live(0, dek0), Live(1, dek1) };

        using var provider = new EpochDekProvider(fileId, activeEpoch: epoch, Table());
        var ct = provider.Encrypt("provider aad binding"u8.ToArray(), blockId, type);

        var decBlockId = blockId;
        var decType = type;
        var decEpoch = epoch;
        EpochDekProvider? decProvider = null;
        try
        {
            switch (moved)
            {
                case "blockId": decBlockId = Id(); break;
                case "blockType": decType = BlockType.EmailMetadata; break;
                case "epoch": decEpoch = 0; break; // present, but a different DEK
                case "fileId":
                    // A different file's provider, same DEK table: only the FileId AAD differs.
                    decProvider = new EpochDekProvider(Id(), activeEpoch: epoch, Table());
                    break;
            }

            var target = decProvider ?? provider;
            var ex = Assert.Throws<WrongKeyOrTamperError>(() =>
                target.Decrypt(ct, decBlockId, decType, decEpoch));
            Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
            Assert.Equal(TamperCause.GcmTag, ex.Cause);
        }
        finally
        {
            decProvider?.Dispose();
        }
    }

    [Fact]
    public void EncryptAndDecrypt_StructurallyRequireAadComponents()
    {
        // "Both operations require AAD" is enforced by the type system, not by a runtime check:
        // FileId is a mandatory constructor parameter (an AAD component for every block), and
        // every Encrypt/Decrypt entry point requires blockId and blockType; Decrypt also
        // requires keyEpoch. There is no overload that omits them.
        var ctor = typeof(EpochDekProvider).GetConstructors().Single();
        Assert.Contains("fileId", ctor.GetParameters().Select(p => p.Name));

        AssertAllOverloadsRequire(typeof(EpochDekProvider), nameof(EpochDekProvider.Encrypt),
            "blockId", "blockType");
        AssertAllOverloadsRequire(typeof(EpochDekProvider), nameof(EpochDekProvider.Decrypt),
            "blockId", "blockType", "keyEpoch");
    }

    private static void AssertAllOverloadsRequire(Type type, string method, params string[] required)
    {
        var overloads = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == method)
            .ToList();
        Assert.NotEmpty(overloads);
        Assert.All(overloads, m =>
        {
            var pnames = m.GetParameters().Select(p => p.Name).ToHashSet();
            foreach (var r in required)
                Assert.Contains(r, pnames);
        });
    }

    [Fact]
    public void Decrypt_PresentEpochWrongKey_IsWrongKeyOrTamper_NotEpochUnavailable()
    {
        var fileId = Id();
        var blockId = Id();
        var dek0 = Dek();
        var dek1 = Dek();
        // Seal under epoch 1, then present a header epoch 0 whose DEK exists but differs:
        // this is an authentication failure, a distinct class from an unavailable epoch.
        using var provider = new EpochDekProvider(fileId, activeEpoch: 1, new[] { Live(0, dek0), Live(1, dek1) });
        var ct = provider.Encrypt("data"u8.ToArray(), blockId, BlockType.EmailContent);

        var ex = Assert.Throws<WrongKeyOrTamperError>(() =>
            provider.Decrypt(ct, blockId, BlockType.EmailContent, keyEpoch: 0));
        Assert.Equal(VerificationFailureKind.WrongKeyOrTamper, ex.Kind);
    }

    // --- Constructor guards ---

    [Fact]
    public void Constructor_ActiveEpochMissing_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new EpochDekProvider(Id(), activeEpoch: 5, new[] { Live(0, Dek()) }));
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void Constructor_ActiveEpochRetired_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new EpochDekProvider(Id(), activeEpoch: 0, new[] { Retired(0) }));
    }

    [Fact]
    public void Constructor_DuplicateEpoch_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new EpochDekProvider(Id(), activeEpoch: 0, new[] { Live(0, Dek()), Live(0, Dek()) }));
    }

    [Fact]
    public void Constructor_BadFileIdLength_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new EpochDekProvider(new byte[15], activeEpoch: 0, new[] { Live(0, Dek()) }));
    }

    [Fact]
    public void EpochDek_LiveDekWrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new EpochDekProvider.EpochDek(0, new byte[16], retired: false));
    }

    // --- Disposal / zeroization ---

    [Fact]
    public void Dispose_ThrowsObjectDisposed_OnEncryptAndDecrypt()
    {
        var fileId = Id();
        var blockId = Id();
        var provider = new EpochDekProvider(fileId, activeEpoch: 0, new[] { Live(0, Dek()) });
        var ct = provider.Encrypt("data"u8.ToArray(), blockId, BlockType.EmailContent);
        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            provider.Encrypt("x"u8.ToArray(), blockId, BlockType.EmailContent));
        Assert.Throws<ObjectDisposedException>(() =>
            provider.Decrypt(ct, blockId, BlockType.EmailContent, keyEpoch: 0));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var provider = new EpochDekProvider(Id(), activeEpoch: 0, new[] { Live(0, Dek()) });
        provider.Dispose();
        provider.Dispose();
        provider.Dispose();
    }

    [Fact]
    public void Dispose_DoesNotZeroizeCallerSuppliedDekBuffers()
    {
        var fileId = Id();
        var dek0 = Dek();
        var dek0Copy = (byte[])dek0.Clone();

        var provider = new EpochDekProvider(fileId, activeEpoch: 0, new[] { Live(0, dek0) });
        provider.Dispose();

        // The provider copies DEKs internally, so the caller's buffer is untouched.
        Assert.Equal(dek0Copy, dek0);
    }

    [Fact]
    public void Dispose_ZeroizesInternalDekBuffers()
    {
        var provider = new EpochDekProvider(Id(), activeEpoch: 1, new[] { Live(0, Dek()), Live(1, Dek()) });

        // Snapshot the internal DEK buffers before disposal; they must be non-zero.
        var internalDeks = GetInternalDeks(provider);
        Assert.NotEmpty(internalDeks);
        Assert.All(internalDeks, d => Assert.Contains(d, b => b != 0));

        provider.Dispose();

        // The same underlying arrays must now be all-zero.
        Assert.All(internalDeks, d => Assert.All(d, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void Dispose_ZeroizesOwnedKek()
    {
        var kek = Dek();
        var provider = new EpochDekProvider(Id(), activeEpoch: 0, new[] { Live(0, Dek()) }, kek);

        var internalKek = GetInternalKek(provider);
        Assert.NotNull(internalKek);
        Assert.Contains(internalKek!, b => b != 0);

        provider.Dispose();

        Assert.All(internalKek!, b => Assert.Equal(0, b));
    }

    // Reflection helpers: the provider owns copies of key material, so we reach the internal
    // buffers to prove Dispose scrubbed them (there is no public accessor by design).
    private static List<byte[]> GetInternalDeks(EpochDekProvider provider)
    {
        var tableField = typeof(EpochDekProvider).GetField("_table", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var table = (System.Collections.IDictionary)tableField.GetValue(provider)!;
        var result = new List<byte[]>();
        foreach (var value in table.Values)
        {
            var dek = (byte[])value!.GetType().GetField("Dek")!.GetValue(value)!;
            if (dek.Length > 0) result.Add(dek);
        }
        return result;
    }

    private static byte[]? GetInternalKek(EpochDekProvider provider)
    {
        var kekField = typeof(EpochDekProvider).GetField("_kek", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[]?)kekField.GetValue(provider);
    }
}
