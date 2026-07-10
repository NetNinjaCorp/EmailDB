using System.Security.Cryptography;
using System.Text;
using EmailDB.Format;
using EmailDB.Format.V3;
using EmailDB.UnitTests.FaultInjection;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the compaction copy pass's optional re-encryption (US-EMDB-90-5,
/// docs/Compaction.md Section 4, spec Section 9.x): with <c>reEncrypt = true</c> every
/// DEK-encrypted live block is decrypted with its original-epoch DEK and re-encrypted
/// under the provider's active epoch with a fresh random nonce and AAD recomputed for
/// the new epoch — the SAME BlockId. With <c>reEncrypt = false</c> the ciphertext is
/// copied verbatim (the pre-existing behavior). Covers the story acceptance criteria:
/// <list type="bullet">
/// <item>reEncrypt=true leaves every encrypted block at the active epoch with fresh nonces;</item>
/// <item>BlockIds are unchanged and the AAD is recomputed with the new epoch (the block
/// decrypts at the new epoch and no longer at the old one);</item>
/// <item>reEncrypt=false copies ciphertext verbatim and moves no block's epoch;</item>
/// <item>the KEK-sealed KeyStore and plaintext blocks are always copied verbatim (the DEK
/// provider never touches them — pruning of retired epochs is US-EMDB-90-6).</item>
/// </list>
/// The primitive tests drive <see cref="Compactor.CopyBlocks"/> over bare files with a
/// hand-built multi-epoch <see cref="EpochDekProvider"/>; the end-to-end test runs the
/// real <see cref="Compactor.Begin"/> + <see cref="Compactor.CopyLiveBlocks"/> over a
/// genuine encrypted file created by <see cref="EmailManager"/>.
/// </summary>
public class CompactorReEncryptTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-reencrypt-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) keep the KDF fast in the E2E test.
    private static Argon2idParams FastParams => new(8, 1, 1);

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();

    public void Dispose()
    {
        Delete(_path);
        Delete(SideFile);
    }

    private static void Delete(string p)
    {
        if (File.Exists(p))
            File.Delete(p);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private static T Require<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : null);
        return result.Value;
    }

    private static BlockManager OpenManager(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return new BlockManager(stream, ownsStream: true);
    }

    // ------------------------------------------------------------- Primitive fixture

    /// <summary>A block written into the primitive-test source file, with what we need to check it.</summary>
    private readonly record struct SourceBlock(
        BlockLocation Location, byte[]? Plaintext, ushort Epoch, bool Encrypted, BlockType Type);

    /// <summary>
    /// Writes a source file carrying: content blocks encrypted under epochs 0, 1, and 2
    /// (the last already at the active epoch), a plaintext block, and a KEK-sealed KeyStore
    /// block — the mix a re-encrypting compaction must handle. Returns the descriptors and
    /// a re-encryption provider whose active epoch is 2 and whose table holds every epoch.
    /// </summary>
    private (List<SourceBlock> Blocks, EpochDekProvider Provider) BuildMultiEpochSource()
    {
        var dek0 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var dek1 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var dek2 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);

        var pEpoch0 = "content sealed under epoch 0"u8.ToArray();
        var pEpoch1 = "content sealed under epoch 1"u8.ToArray();
        var pEpoch2 = "content already at the active epoch 2"u8.ToArray();
        var pPlain = "plaintext block, never encrypted"u8.ToArray();

        var blocks = new List<SourceBlock>();

        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var manager = new BlockManager(stream, ownsStream: true))
        {
            BlockLocation AppendUnder(ushort activeEpoch, (ushort Epoch, byte[] Dek)[] table, byte[] plaintext)
            {
                using var provider = new EpochDekProvider(FileId, activeEpoch,
                    table.Select(t => new EpochDekProvider.EpochDek(t.Epoch, t.Dek)).ToArray());
                var store = new EncryptedBlockStore(manager, provider);
                return Require(store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintext));
            }

            var b0 = AppendUnder(0, new[] { ((ushort)0, dek0) }, pEpoch0);
            blocks.Add(new SourceBlock(b0, pEpoch0, 0, true, BlockType.EmailContent));

            var b1 = AppendUnder(1, new[] { ((ushort)0, dek0), ((ushort)1, dek1) }, pEpoch1);
            blocks.Add(new SourceBlock(b1, pEpoch1, 1, true, BlockType.EmailContent));

            var b2 = AppendUnder(2,
                new[] { ((ushort)0, dek0), ((ushort)1, dek1), ((ushort)2, dek2) }, pEpoch2);
            blocks.Add(new SourceBlock(b2, pEpoch2, 2, true, BlockType.EmailContent));

            // A plaintext block: re-encryption must leave it untouched.
            var bp = Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, pPlain));
            blocks.Add(new SourceBlock(bp, pPlain, 0, false, BlockType.EmailContent));

            // A KEK-sealed KeyStore block carrying all three DEKs: the re-encryption path must
            // copy it verbatim (it is not DEK-encrypted, so the provider cannot decrypt it).
            var kek = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
            var keyStore = new KeyStoreBlock
            {
                ActiveEpoch = 2,
                Entries =
                {
                    new KeyStoreEntry { Epoch = 0, Dek = dek0 },
                    new KeyStoreEntry { Epoch = 1, Dek = dek1 },
                    new KeyStoreEntry { Epoch = 2, Dek = dek2 },
                },
            };
            var ks = Require(EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, keyStore));
            blocks.Add(new SourceBlock(ks, null, 0, true, BlockType.KeyStore));

            Ok(manager.Flush());
        }

        var reEncryptProvider = new EpochDekProvider(FileId, activeEpoch: 2, new[]
        {
            new EpochDekProvider.EpochDek(0, dek0),
            new EpochDekProvider.EpochDek(1, dek1),
            new EpochDekProvider.EpochDek(2, dek2),
        });

        return (blocks, reEncryptProvider);
    }

    private static List<BlockLocation> Live(IEnumerable<SourceBlock> blocks) =>
        blocks.Select(b => b.Location).ToList();

    // ------------------------------------------------------ reEncrypt = true (AC 90-1, 90-2)

    [Fact]
    public void ReEncrypt_moves_every_encrypted_block_to_the_active_epoch_with_fresh_nonces_and_same_ids()
    {
        var (blocks, provider) = BuildMultiEpochSource();
        var destPath = _path + ".re";
        try
        {
            using (provider)
            using (var srcMgr = OpenManager(_path))
            using (var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var dstMgr = new BlockManager(dstStream, ownsStream: true))
            {
                var copied = Require(Compactor.CopyBlocks(srcMgr, dstMgr, Live(blocks), provider));
                Ok(dstMgr.Flush());

                // Same BlockIds, same count, same order — pointers are unchanged.
                Assert.Equal(blocks.Count, copied.Count);
                for (int i = 0; i < blocks.Count; i++)
                    Assert.Equal(blocks[i].Location.BlockId, copied[i].BlockId);

                var byId = blocks.ToDictionary(b => Convert.ToHexString(b.Location.BlockId));
                foreach (var cb in copied)
                {
                    var source = byId[Convert.ToHexString(cb.BlockId)];
                    var dst = Require(dstMgr.Read(cb.NewOffset));
                    var src = Require(srcMgr.Read(source.Location.Offset));

                    // BlockId, type, and total length are preserved in every mode.
                    Assert.Equal(source.Location.BlockId, dst.Header.BlockId);
                    Assert.Equal(source.Type, dst.Header.Type);
                    Assert.Equal(source.Location.TotalBlockLength, cb.NewLength);

                    if (source is { Encrypted: true, Type: BlockType.EmailContent })
                    {
                        // Every encrypted content block now sits at the active epoch (2)...
                        Assert.True(dst.Header.IsEncrypted);
                        Assert.Equal((ushort)2, dst.Header.KeyEpoch);
                        // ...with a fresh 12-byte GCM nonce. The nonce is the payload prefix
                        // (Nonce ‖ Ciphertext ‖ Tag); asserting the prefix itself changed proves
                        // a fresh nonce directly — not merely a differing ciphertext body, which a
                        // key change alone would also produce (and which cannot fire for the block
                        // already at the active epoch, where the DEK is unchanged).
                        var srcNonce = src.Payload.AsSpan(0, AesGcmBlockCipher.NonceSize).ToArray();
                        var dstNonce = dst.Payload.AsSpan(0, AesGcmBlockCipher.NonceSize).ToArray();
                        Assert.NotEqual(srcNonce, dstNonce);
                        // ...and the on-disk ciphertext differs from the source overall.
                        Assert.NotEqual(src.Payload, dst.Payload);
                        // ...and it decrypts at the NEW epoch back to the original plaintext.
                        var plain = provider.Decrypt(dst.Payload, dst.Header.BlockId, dst.Header.Type, dst.Header.KeyEpoch);
                        Assert.Equal(source.Plaintext, plain);
                    }
                }
            }
        }
        finally
        {
            Delete(destPath);
        }
    }

    [Fact]
    public void ReEncrypt_rebinds_the_aad_so_the_old_epoch_no_longer_authenticates()
    {
        var (blocks, provider) = BuildMultiEpochSource();
        var destPath = _path + ".re";
        try
        {
            using (provider)
            using (var srcMgr = OpenManager(_path))
            using (var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var dstMgr = new BlockManager(dstStream, ownsStream: true))
            {
                var copied = Require(Compactor.CopyBlocks(srcMgr, dstMgr, Live(blocks), provider));
                Ok(dstMgr.Flush());

                // Take the block that was written under epoch 0. In the side file it must decrypt
                // at epoch 2 (its new AAD/epoch) but NOT at its former epoch 0 — proving the AAD
                // was recomputed for the new epoch, not merely re-tagged.
                var epoch0 = blocks.Single(b => b is { Encrypted: true, Epoch: 0, Type: BlockType.EmailContent });
                var cb = copied.Single(c => c.BlockId.AsSpan().SequenceEqual(epoch0.Location.BlockId));
                var dst = Require(dstMgr.Read(cb.NewOffset));

                Assert.Equal((ushort)2, dst.Header.KeyEpoch);
                var atNew = provider.Decrypt(dst.Payload, dst.Header.BlockId, dst.Header.Type, keyEpoch: 2);
                Assert.Equal(epoch0.Plaintext, atNew);

                // The epoch-0 DEK with epoch-0 AAD: GCM/AAD authentication must fail (the ciphertext
                // is bound to epoch 2 now), proving the AAD was rebound, not merely re-tagged.
                Assert.Throws<WrongKeyOrTamperError>(() =>
                    provider.Decrypt(dst.Payload, dst.Header.BlockId, dst.Header.Type, keyEpoch: 0));
            }
        }
        finally
        {
            Delete(destPath);
        }
    }

    [Fact]
    public void ReEncrypt_copies_plaintext_and_keystore_blocks_verbatim()
    {
        var (blocks, provider) = BuildMultiEpochSource();
        var destPath = _path + ".re";
        try
        {
            using (provider)
            using (var srcMgr = OpenManager(_path))
            using (var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var dstMgr = new BlockManager(dstStream, ownsStream: true))
            {
                var copied = Require(Compactor.CopyBlocks(srcMgr, dstMgr, Live(blocks), provider));
                Ok(dstMgr.Flush());

                var byId = copied.ToDictionary(c => Convert.ToHexString(c.BlockId));

                // The plaintext block is unchanged: same flag (none), same bytes.
                var plain = blocks.Single(b => !b.Encrypted);
                var plainSrc = Require(srcMgr.Read(plain.Location.Offset));
                var plainDst = Require(dstMgr.Read(byId[Convert.ToHexString(plain.Location.BlockId)].NewOffset));
                Assert.False(plainDst.Header.IsEncrypted);
                Assert.Equal(plainSrc.Payload, plainDst.Payload);

                // The KEK-sealed KeyStore is copied byte-for-byte and stays at its own epoch 0:
                // the DEK provider never decrypts it (that would have failed the copy outright).
                var keyStore = blocks.Single(b => b.Type == BlockType.KeyStore);
                var ksSrc = Require(srcMgr.Read(keyStore.Location.Offset));
                var ksDst = Require(dstMgr.Read(byId[Convert.ToHexString(keyStore.Location.BlockId)].NewOffset));
                Assert.True(ksDst.Header.IsEncrypted);
                Assert.Equal(BlockType.KeyStore, ksDst.Header.Type);
                Assert.Equal((ushort)0, ksDst.Header.KeyEpoch);
                Assert.Equal(ksSrc.Payload, ksDst.Payload);
            }
        }
        finally
        {
            Delete(destPath);
        }
    }

    // ------------------------------------------------------------ reEncrypt = false (AC 90-4)

    [Fact]
    public void Verbatim_copy_leaves_ciphertext_and_epochs_untouched()
    {
        var (blocks, provider) = BuildMultiEpochSource();
        var destPath = _path + ".vb";
        try
        {
            provider.Dispose(); // verbatim needs no key at all
            using (var srcMgr = OpenManager(_path))
            using (var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var dstMgr = new BlockManager(dstStream, ownsStream: true))
            {
                // No provider argument => verbatim, the pre-existing behavior.
                var copied = Require(Compactor.CopyBlocks(srcMgr, dstMgr, Live(blocks)));
                Ok(dstMgr.Flush());

                var byId = blocks.ToDictionary(b => Convert.ToHexString(b.Location.BlockId));
                foreach (var cb in copied)
                {
                    var source = byId[Convert.ToHexString(cb.BlockId)];
                    var src = Require(srcMgr.Read(source.Location.Offset));
                    var dst = Require(dstMgr.Read(cb.NewOffset));

                    // Byte-for-byte identical, and each block keeps its original epoch (0, 1, or 2).
                    Assert.Equal(src.Header.KeyEpoch, dst.Header.KeyEpoch);
                    Assert.Equal(source.Epoch, dst.Header.KeyEpoch);
                    Assert.Equal(src.Header.Flags, dst.Header.Flags);
                    Assert.Equal(src.Payload, dst.Payload);
                }
            }
        }
        finally
        {
            Delete(destPath);
        }
    }

    // --------------------------------------------------------------- Defensive guard

    [Fact]
    public void ReEncrypt_fails_when_the_provider_lacks_a_source_epoch()
    {
        var (blocks, provider) = BuildMultiEpochSource();
        var destPath = _path + ".miss";
        try
        {
            provider.Dispose();
            // A provider missing epoch 1 (only 0 and 2) cannot decrypt the epoch-1 block.
            var dek0 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
            var dek2 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
            using var partial = new EpochDekProvider(FileId, activeEpoch: 2, new[]
            {
                new EpochDekProvider.EpochDek(0, dek0),
                new EpochDekProvider.EpochDek(2, dek2),
            });

            using var srcMgr = OpenManager(_path);
            using var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var dstMgr = new BlockManager(dstStream, ownsStream: true);

            var copy = Compactor.CopyBlocks(srcMgr, dstMgr, Live(blocks), partial);
            Assert.True(copy.IsFailure, "re-encryption must fail when an epoch's DEK is unavailable");
        }
        finally
        {
            Delete(destPath);
        }
    }

    // ------------------------------------------------ Instance method: guard + end-to-end

    [Fact]
    public void CopyLiveBlocks_reEncrypt_true_without_a_provider_is_rejected()
    {
        new V3TestFileBuilder().WithFillerBlocks(4).Build(_path);

        using var compactor = Require(Compactor.Begin(_path));
        var result = compactor.CopyLiveBlocks(reEncrypt: true);
        Assert.True(result.IsFailure, "reEncrypt=true without a provider must be refused");
    }

    [Fact]
    public void CopyLiveBlocks_reEncrypt_re_encrypts_the_live_blocks_of_a_real_encrypted_file()
    {
        const string password = "compaction re-encryption end to end";
        var mimeA = Encoding.UTF8.GetBytes("MIME body A: " + new string('a', 200));
        var mimeB = Encoding.UTF8.GetBytes("MIME body B: " + new string('b', 200));

        // --- Build a genuine, cleanly-openable encrypted file with two encrypted content blocks.
        var createOptions = new EmailManagerCreateOptions { Password = password, KdfParameters = FastParams };
        Require(EmailManager.Create(_path, createOptions)).Dispose();
        using (var mgr = Require(EmailManager.Open(_path, new EmailManagerOpenOptions { Password = password })))
        {
            var folder = FolderPageDirectory.Create(new UlidGenerator().Next(), Array.Empty<PageEntry>());
            long ticks = new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc).Ticks;
            AddEmailRequest Req(byte[] mime) => new()
            {
                RawContent = mime,
                Folder = folder,
                MetadataPayload = Encoding.UTF8.GetBytes("tier2"),
                DateTicks = ticks,
                Flags = ListingFlags.Read,
                From = "sender@example.com",
                Subject = "probe",
                Preview = "preview",
            };
            Ok(mgr.AddEmail(Req(mimeA)));
            Ok(mgr.AddEmail(Req(mimeB)));
            Ok(mgr.Commit());
        }

        // --- Derive the source's DEK provider from file + password (real password verification),
        //     on a handle we close before compaction opens the file.
        using var provider = DeriveProvider(password);

        // --- Compact with re-encryption. Begin opens the file cleanly (a Default-policy file's
        //     structural blocks are plaintext, so open needs no key); the copy pass re-encrypts
        //     every DEK-encrypted live block at the provider's active epoch.
        var decrypted = new List<byte[]>();
        using (var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped())))
        {
            // The re-encrypting copy succeeds over a genuine encrypted file. Any KEK-sealed
            // KeyStore in the live set is copied verbatim (feeding it to the DEK provider would
            // have thrown and failed the copy).
            Ok(compactor.CopyLiveBlocks(reEncrypt: true, provider));

            foreach (var cb in compactor.CopiedBlocks)
            {
                var block = Require(compactor.DestBlockManager.Read(cb.NewOffset));
                if (!block.Header.IsEncrypted || block.Header.Type == BlockType.KeyStore)
                    continue;

                // Every re-encrypted block carries the active epoch and decrypts through the provider.
                Assert.Equal(provider.ActiveEpoch, block.Header.KeyEpoch);
                decrypted.Add(provider.Decrypt(
                    block.Payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch));
            }
        }

        // The original MIME bodies are recovered from the re-encrypted side-file content blocks.
        Assert.Contains(decrypted, d => d.AsSpan().SequenceEqual(mimeA));
        Assert.Contains(decrypted, d => d.AsSpan().SequenceEqual(mimeB));
    }

    /// <summary>Runs the real bootstrap (file + password) on a handle closed before it returns.</summary>
    private EpochDekProvider DeriveProvider(string password)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Superblock superblock;
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
            superblock = Require(sbManager.Load());
        using var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);
        return Require(EncryptionBootstrap.Open(superblock, password, manager));
    }

    /// <summary>
    /// A no-op <see cref="IEncryptionBootstrap"/> for a test that has already derived the DEK
    /// provider itself. A Default-policy file's structural blocks (Checkpoint, B+-tree nodes)
    /// are plaintext, so the open path resolves the roots without any key — the provider is only
    /// needed for the content re-encryption the copy pass performs afterward.
    /// </summary>
    private sealed class AlreadyBootstrapped : IEncryptionBootstrap
    {
        public Result Bootstrap(Superblock superblock) => Result.Success();
    }
}
