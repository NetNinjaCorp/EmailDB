using System.Buffers.Binary;
using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for DEK pruning during a re-encrypting compaction (US-EMDB-90-6,
/// docs/Compaction.md Section 4): after the copy pass moves every DEK-encrypted block onto the
/// active epoch, the rebuild re-seals the compacted file's KeyStore with every non-active epoch
/// that has zero remaining block references RETIRED — its 32 DEK bytes dropped. Covers the story
/// acceptance criteria:
/// <list type="bullet">
/// <item>DEKs with zero remaining references are pruned from the new file's KeyStore;</item>
/// <item>the active epoch's DEK is always retained (new blocks are still written under it);</item>
/// <item><c>reEncrypt = false</c> (and any copy without the KEK) copies the KeyStore verbatim and prunes nothing;</item>
/// <item>a pruned epoch's DEK is genuinely absent from the new file's KeyStore — the reopened
/// provider has no live key for it — while the file still opens and its content still decrypts.</item>
/// </list>
///
/// <para>Each test builds a genuine, cleanly-openable encrypted file: content blocks sealed under
/// epochs 0 and 1 (with a two-epoch KeyStore, active epoch 1), a committed
/// <see cref="BlockLocationIndex"/>, and a Checkpoint whose KeyStoreRoot names the multi-epoch
/// KeyStore. The file's structural blocks are plaintext, so <see cref="Compactor.Begin"/> opens it
/// without a key; the copy pass re-encrypts the content under the KEK-independent DEK provider and
/// the rebuild prunes using the supplied KEK.</para>
/// </summary>
public class CompactorDekPruningTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-dekprune-{Guid.NewGuid():N}.emdb");

    private string SideFile => _path + Compactor.SideFileSuffix;

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) keep the KDF fast.
    private static Argon2idParams FastParams => new(8, 1, 1);

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xD0 + i)).ToArray();

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

    // ---------------------------------------------------------------- Source fixture

    /// <summary>What a built source file exposes: the KEK it was sealed with, the DEK provider a
    /// re-encrypting compaction uses (both epochs live, active 1), and the plaintexts written.</summary>
    private sealed record SourceFile(byte[] Kek, byte[] Dek0, byte[] Dek1, List<byte[]> Plaintexts);

    private const string Password = "dek pruning end to end";

    /// <summary>
    /// Builds a cleanly-openable encrypted file with content blocks under epochs 0 and 1, a
    /// two-epoch KeyStore (active epoch 1) sealed under a password-derived KEK, a committed location
    /// index over the content, and a Checkpoint naming the KeyStore. The returned provider holds both
    /// DEKs (active epoch 1) — the source's provider a re-encrypting compaction is handed.
    /// </summary>
    private SourceFile BuildSource()
    {
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var kek = PasswordKeyDerivation.DeriveKek(Password, FastParams, salt);
        var token = KeyVerificationToken.Create(kek);
        var dek0 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var dek1 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);

        var plaintexts = new List<byte[]>
        {
            "content sealed under epoch 0 (a)"u8.ToArray(),
            "content sealed under epoch 0 (b)"u8.ToArray(),
            "content sealed under epoch 1 (a)"u8.ToArray(),
            "content sealed under epoch 1 (b)"u8.ToArray(),
        };

        BlockLocation keyStoreLoc;
        CheckpointRootPointer hint;
        var runtimeMap = new RuntimeBlockOffsetMap();
        using (var stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var manager = new BlockManager(stream, offsetMap: runtimeMap, ownsStream: true))
        {
            var dataBlocks = new List<BlockLocation>();

            using (var p0 = new EpochDekProvider(FileId, 0, new[] { new EpochDekProvider.EpochDek(0, dek0) }))
            {
                var s0 = new EncryptedBlockStore(manager, p0);
                dataBlocks.Add(Require(s0.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintexts[0])));
                dataBlocks.Add(Require(s0.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintexts[1])));
            }
            using (var p1 = new EpochDekProvider(FileId, 1,
                new[] { new EpochDekProvider.EpochDek(0, dek0), new EpochDekProvider.EpochDek(1, dek1) }))
            {
                var s1 = new EncryptedBlockStore(manager, p1);
                dataBlocks.Add(Require(s1.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintexts[2])));
                dataBlocks.Add(Require(s1.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintexts[3])));
            }

            // A plaintext folder root (directly addressed by the Checkpoint, not a location-index entry).
            var folder = Require(manager.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, new byte[64]));
            var folderPointer = CheckpointRootPointer.Create(folder.BlockId, folder.Offset);

            // The two-epoch KeyStore, active epoch 1, KEK-sealed.
            var keyStore = new KeyStoreBlock
            {
                ActiveEpoch = 1,
                Entries =
                {
                    new KeyStoreEntry { Epoch = 0, Dek = dek0 },
                    new KeyStoreEntry { Epoch = 1, Dek = dek1 },
                },
            };
            keyStoreLoc = Require(EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, keyStore));
            var keyStorePointer = CheckpointRootPointer.Create(keyStoreLoc.BlockId, keyStoreLoc.Offset);

            // Commit a location index over the content blocks.
            var nodeStore = new BTreeNodeStore(manager, blockIdResolver: null);
            var index = new BlockLocationIndex(nodeStore, maxLeafEntries: 4, maxInternalKeys: 3);
            Ok(index.PutBatch(dataBlocks));
            Ok(manager.Flush());

            long locationRootOffset = BinaryPrimitives.ReadInt64LittleEndian(index.Root!.RootRef.Reference);
            var rootBlockId = runtimeMap.SnapshotOrderedByOffset().First(b => b.Offset == locationRootOffset).BlockId;
            var locationPointer = CheckpointRootPointer.Create(rootBlockId, locationRootOffset);

            var writer = new CheckpointWriter(manager, FileId);
            Ok(writer.WriteCheckpoint(new CheckpointContents
            {
                FolderTreeRoot = folderPointer,
                PrimaryIndexRoot = CheckpointRootPointer.None,
                LocationIndexRoot = locationPointer,
                MetadataRoot = CheckpointRootPointer.None,
                KeyStoreRoot = keyStorePointer,
                SecondaryIndexes = Array.Empty<CheckpointSecondaryIndex>(),
                LiveBlockCount = dataBlocks.Count,
                LiveByteCount = 4096,
                DeadByteCount = 0,
            }));
            hint = writer.LastCheckpointPointer;
            Ok(manager.Flush());
        }

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
        {
            Superblock Make() => new()
            {
                FileId = (byte[])FileId.Clone(),
                CleanShutdown = 1,
                LastCheckpointBlockId = (byte[])hint.BlockId.Clone(),
                LastCheckpointOffset = hint.Offset,
                EncryptionEnabled = 1,
                AlgorithmId = EncryptionBootstrap.AesGcmAlgorithmId,
                KdfType = (byte)KdfType.Argon2id,
                KdfParams = FastParams.Pack(),
                Salt = (byte[])salt.Clone(),
                KeyVerificationToken = (byte[])token.Clone(),
                ActiveKeyStoreBlockId = (byte[])keyStoreLoc.BlockId.Clone(),
                ActiveKeyStoreOffset = keyStoreLoc.Offset,
            };
            Ok(sbManager.Write(Make()));
            Ok(sbManager.Write(Make())); // fill both slots
        }

        return new SourceFile(kek, dek0, dek1, plaintexts);
    }

    private EpochDekProvider ReEncryptProvider(SourceFile src) =>
        new(FileId, activeEpoch: 1, new[]
        {
            new EpochDekProvider.EpochDek(0, src.Dek0),
            new EpochDekProvider.EpochDek(1, src.Dek1),
        });

    /// <summary>Reads and KEK-decrypts the KeyStore the side file's fresh Checkpoint names.</summary>
    private static KeyStoreBlock ReadSideFileKeyStore(Compactor compactor, byte[] kek, out byte[] payload)
    {
        var ptr = compactor.NewCheckpoint!.KeyStoreRoot;
        var block = Require(compactor.DestBlockManager.Read(ptr.Offset));
        payload = AesGcmBlockCipher.Decrypt(
            block.Payload, kek, FileId, block.Header.BlockId, BlockType.KeyStore, block.Header.KeyEpoch);
        return Require(KeyStoreSerializer.Deserialize(payload));
    }

    /// <summary>A no-op bootstrap: this file's structural blocks are plaintext, so the open path
    /// resolves every root without a key (the DEK provider/KEK are supplied to the copy/prune passes).</summary>
    private sealed class AlreadyBootstrapped : IEncryptionBootstrap
    {
        public Result Bootstrap(Superblock superblock) => Result.Success();
    }

    // ----------------------------------------------------------------------- Tests

    [Fact]
    public void ReEncrypt_prunes_the_unreferenced_epoch_dek_from_the_new_keystore()
    {
        var src = BuildSource();
        using var provider = ReEncryptProvider(src);
        using var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped()));

        Ok(compactor.CopyLiveBlocks(reEncrypt: true, provider, src.Kek));
        Ok(compactor.RebuildLocationIndexAndWriteCheckpoint());

        // Every content block moved to epoch 1, so epoch 0 has zero references and is pruned.
        Assert.Equal(new ushort[] { 0 }, compactor.PrunedEpochs);

        var table = ReadSideFileKeyStore(compactor, src.Kek, out _);
        var epoch0 = table.Entries.Single(e => e.Epoch == 0);
        Assert.True(epoch0.Retired, "epoch 0 (zero references) must be retired in the new file's KeyStore");
        Assert.Empty(epoch0.Dek);
    }

    [Fact]
    public void ReEncrypt_retains_the_active_epoch_dek()
    {
        var src = BuildSource();
        using var provider = ReEncryptProvider(src);
        using var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped()));

        Ok(compactor.CopyLiveBlocks(reEncrypt: true, provider, src.Kek));
        Ok(compactor.RebuildLocationIndexAndWriteCheckpoint());

        var table = ReadSideFileKeyStore(compactor, src.Kek, out _);
        Assert.Equal((ushort)1, table.ActiveEpoch);
        var epoch1 = table.Entries.Single(e => e.Epoch == 1);
        Assert.False(epoch1.Retired, "the active epoch's DEK must never be pruned");
        Assert.True(src.Dek1.AsSpan().SequenceEqual(epoch1.Dek), "the active epoch keeps its exact DEK");
        Assert.DoesNotContain((ushort)1, compactor.PrunedEpochs);
    }

    [Fact]
    public void Verbatim_copy_prunes_nothing()
    {
        var src = BuildSource();
        using var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped()));

        // reEncrypt = false: the KeyStore is copied byte-for-byte, every epoch retained.
        Ok(compactor.CopyLiveBlocks());
        Ok(compactor.RebuildLocationIndexAndWriteCheckpoint());

        Assert.Empty(compactor.PrunedEpochs);

        var table = ReadSideFileKeyStore(compactor, src.Kek, out _);
        Assert.Equal(2, table.Entries.Count);
        Assert.All(table.Entries, e => Assert.False(e.Retired));
        var epoch0 = table.Entries.Single(e => e.Epoch == 0);
        Assert.True(src.Dek0.AsSpan().SequenceEqual(epoch0.Dek), "a verbatim copy leaves epoch 0's DEK intact");
    }

    [Fact]
    public void ReEncrypt_without_a_kek_prunes_nothing()
    {
        var src = BuildSource();
        using var provider = ReEncryptProvider(src);
        using var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped()));

        // Re-encrypting but no KEK supplied: pruning cannot re-seal the KeyStore, so it copies it
        // verbatim (the safe fallback) and prunes nothing.
        Ok(compactor.CopyLiveBlocks(reEncrypt: true, provider));
        Ok(compactor.RebuildLocationIndexAndWriteCheckpoint());

        Assert.Empty(compactor.PrunedEpochs);
        var table = ReadSideFileKeyStore(compactor, src.Kek, out _);
        Assert.All(table.Entries, e => Assert.False(e.Retired));
    }

    [Fact]
    public void Pruned_epoch_dek_is_genuinely_absent_from_the_new_keystore_payload()
    {
        var src = BuildSource();
        using var provider = ReEncryptProvider(src);
        using var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped()));

        Ok(compactor.CopyLiveBlocks(reEncrypt: true, provider, src.Kek));
        Ok(compactor.RebuildLocationIndexAndWriteCheckpoint());

        var table = ReadSideFileKeyStore(compactor, src.Kek, out var payload);
        try
        {
            // The pruned epoch's 32 DEK bytes appear nowhere in the decrypted KeyStore payload...
            Assert.False(ContainsSubsequence(payload, src.Dek0),
                "the pruned epoch-0 DEK bytes must not survive anywhere in the new KeyStore payload");
            // ...while the retained active epoch's DEK is still present.
            Assert.True(ContainsSubsequence(payload, src.Dek1),
                "the active epoch-1 DEK must remain in the new KeyStore payload");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            foreach (var e in table.Entries)
                CryptographicOperations.ZeroMemory(e.Dek);
        }
    }

    [Fact]
    public void Compacted_file_reopens_with_the_pruned_keystore_and_content_still_decrypts()
    {
        var src = BuildSource();
        using (var provider = ReEncryptProvider(src))
        using (var compactor = Require(Compactor.Begin(_path, new AlreadyBootstrapped())))
        {
            Ok(compactor.CopyLiveBlocks(reEncrypt: true, provider, src.Kek));
            Ok(compactor.RebuildLocationIndexAndWriteCheckpoint());
            Ok(compactor.FinalizeAndSwap());
        }

        // Reopen the compacted file from file + password only: the superblock's KeyStore pointer was
        // remapped to the re-sealed, pruned KeyStore, so the real bootstrap builds a provider that has
        // the active epoch 1 but NO live DEK for the pruned epoch 0.
        Superblock reloaded;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var sbManager = new SuperblockManager(stream, ownsStream: false))
            reloaded = Require(sbManager.Load());

        EpochDekProvider reopened;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var manager = new BlockManager(stream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false))
            reopened = Require(EncryptionBootstrap.Open(reloaded, Password, manager));

        using (reopened)
        {
            Assert.Equal((ushort)1, reopened.ActiveEpoch);
            Assert.True(reopened.HasLiveDek(1), "the active epoch's DEK must survive compaction");
            Assert.False(reopened.HasLiveDek(0), "the pruned epoch's DEK must be gone from the reopened file");

            // Walk the compacted file's live blocks and decrypt every content block — all now at the
            // active epoch — proving the re-encrypted content is readable with only the retained DEK.
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var opened = Require(CleanOpener.Open(stream, new AlreadyBootstrapped()));
            Assert.Equal(OpenOutcomeKind.CleanOpen, opened.Kind);
            using var state = opened.State!;

            var live = Require(Compactor.WalkLiveBlocks(state.LocationIndex));
            var decrypted = new List<byte[]>();
            foreach (var loc in live)
            {
                var block = Require(state.BlockManager.Read(loc.Offset));
                if (!block.Header.IsEncrypted || block.Header.Type != BlockType.EmailContent)
                    continue;
                Assert.Equal((ushort)1, block.Header.KeyEpoch);
                decrypted.Add(reopened.Decrypt(block.Payload, block.Header.BlockId, block.Header.Type, block.Header.KeyEpoch));
            }

            foreach (var plaintext in src.Plaintexts)
                Assert.Contains(decrypted, d => d.AsSpan().SequenceEqual(plaintext));
        }
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }
}
