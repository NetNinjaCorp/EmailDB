using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for the open-time encryption bootstrap (<see cref="EncryptionBootstrap"/>,
/// <see cref="EncryptedBlockStore"/>, <see cref="KeyStoreSerializer"/>, task US-EMDB-76-8,
/// docs/Encryption.md Section 2, spec Section 10.2 step 2): the full chain from superblock
/// fields + password to a live <see cref="EpochDekProvider"/> wired into the block layer.
/// Covers:
/// <list type="bullet">
/// <item>an encrypted file round-trips through close/reopen using only file + password;</item>
/// <item>a multi-epoch DEK table loads and decrypts blocks written under an OLD epoch;</item>
/// <item>KDF parameters are read from the superblock and honored even when non-default;</item>
/// <item>a wrong password fast-fails with the distinct KeyVerificationToken error;</item>
/// <item>the <see cref="PasswordEncryptionBootstrap"/> seam fills the CleanOpener hook.</item>
/// </list>
/// Tests use minimal Argon2id costs so key derivation is fast; production defaults are 64 MB.
/// </summary>
public class EncryptionBootstrapTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-encbootstrap-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    // Cheap Argon2id costs (8 KB, 1 iteration, 1 lane) — keeps the KDF fast in tests.
    private static Argon2idParams FastParams => new(8, 1, 1);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static void Ok(Result r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);
    private static void Ok<T>(Result<T> r) => Assert.True(r.IsSuccess, r.IsFailure ? r.Error : null);

    private FileStream OpenRW() =>
        new(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

    private void WriteSuperblock(Action<Superblock> configure)
    {
        using var stream = OpenRW();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        Superblock Make()
        {
            var sb = new Superblock { FileId = (byte[])FileId.Clone(), CleanShutdown = 1 };
            configure(sb);
            return sb;
        }
        Ok(sbManager.Write(Make()));
        Ok(sbManager.Write(Make())); // fill both slots
    }

    private Superblock LoadSuperblock()
    {
        using var stream = OpenRW();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        var loaded = sbManager.Load();
        Ok(loaded);
        return loaded.Value;
    }

    // -------------------------------------------------------------------------

    [Fact]
    public void EncryptedFile_RoundTrips_FromFileAndPasswordOnly()
    {
        const string password = "correct horse battery staple";
        var plaintext = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        long dataOffset;

        // --- Create: generate salt/DEK, write KeyStore + encrypted data block, write superblock.
        EncryptionBootstrap.CreatedEncryption created;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;

            var store = new EncryptedBlockStore(manager, created.Provider);
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintext);
            Ok(appended);
            dataOffset = appended.Value.Offset;
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        // --- Reopen with only the file + the password.
        var superblock = LoadSuperblock();
        Assert.Equal((byte)1, superblock.EncryptionEnabled);

        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);

        var opened = EncryptionBootstrap.Open(superblock, password, reopenManager);
        Ok(opened);
        using var provider = opened.Value;

        var reopenStore = new EncryptedBlockStore(reopenManager, provider);
        var decrypted = reopenStore.ReadDecrypted(dataOffset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);
    }

    [Fact]
    public void MultiEpochTable_LoadsAndDecrypts_OldEpochBlocks()
    {
        const string password = "rotation test password";
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var dek0 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var dek1 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var kek = PasswordKeyDerivation.DeriveKek(password, FastParams, salt);
        var token = KeyVerificationToken.Create(kek);

        var epoch0Plaintext = "written under epoch 0 (pre-rotation)"u8.ToArray();
        var epoch1Plaintext = "written under epoch 1 (post-rotation)"u8.ToArray();
        long epoch0Offset, epoch1Offset;
        BlockLocation activeKeyStore;

        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            // Epoch 0: KeyStore with just DEK 0, then a data block under epoch 0.
            var ks0 = new KeyStoreBlock
            {
                ActiveEpoch = 0,
                Entries = { new KeyStoreEntry { Epoch = 0, Dek = dek0 } },
            };
            Ok(EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, ks0));

            using (var provider0 = new EpochDekProvider(
                FileId, activeEpoch: 0, new[] { new EpochDekProvider.EpochDek(0, dek0) }))
            {
                var store0 = new EncryptedBlockStore(manager, provider0);
                var a = store0.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, epoch0Plaintext);
                Ok(a);
                epoch0Offset = a.Value.Offset;
            }

            // Rotate: append a KeyStore carrying BOTH epochs, active epoch 1, then a block under epoch 1.
            var ks1 = new KeyStoreBlock
            {
                ActiveEpoch = 1,
                Entries =
                {
                    new KeyStoreEntry { Epoch = 0, Dek = dek0 },
                    new KeyStoreEntry { Epoch = 1, Dek = dek1 },
                },
            };
            var ks1Written = EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, ks1);
            Ok(ks1Written);
            activeKeyStore = ks1Written.Value;

            using (var provider1 = new EpochDekProvider(
                FileId, activeEpoch: 1,
                new[] { new EpochDekProvider.EpochDek(0, dek0), new EpochDekProvider.EpochDek(1, dek1) }))
            {
                var store1 = new EncryptedBlockStore(manager, provider1);
                var b = store1.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, epoch1Plaintext);
                Ok(b);
                epoch1Offset = b.Value.Offset;
            }

            Ok(manager.Flush());
        }

        WriteSuperblock(sb =>
        {
            sb.EncryptionEnabled = 1;
            sb.AlgorithmId = EncryptionBootstrap.AesGcmAlgorithmId;
            sb.KdfType = (byte)KdfType.Argon2id;
            sb.KdfParams = FastParams.Pack();
            sb.Salt = salt;
            sb.KeyVerificationToken = token;
            sb.ActiveKeyStoreBlockId = activeKeyStore.BlockId;
            sb.ActiveKeyStoreOffset = activeKeyStore.Offset;
        });

        // --- Reopen: the loaded table must hold BOTH epochs and decrypt the old-epoch block.
        var superblock = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);

        var opened = EncryptionBootstrap.Open(superblock, password, reopenManager);
        Ok(opened);
        using var provider = opened.Value;

        Assert.Equal((ushort)1, provider.ActiveEpoch);
        Assert.True(provider.HasLiveDek(0), "epoch 0 DEK must survive rotation and load");
        Assert.True(provider.HasLiveDek(1), "epoch 1 DEK must load");

        // The block headers must record the epoch each was encrypted under.
        Assert.Equal((ushort)0, reopenManager.Read(epoch0Offset).Value.Header.KeyEpoch);
        Assert.Equal((ushort)1, reopenManager.Read(epoch1Offset).Value.Header.KeyEpoch);

        var store = new EncryptedBlockStore(reopenManager, provider);
        var oldDecrypted = store.ReadDecrypted(epoch0Offset);
        Ok(oldDecrypted);
        Assert.Equal(epoch0Plaintext, oldDecrypted.Value);

        var newDecrypted = store.ReadDecrypted(epoch1Offset);
        Ok(newDecrypted);
        Assert.Equal(epoch1Plaintext, newDecrypted.Value);
    }

    [Fact]
    public void ThreeEpochTable_LoadedFromDisk_DecryptsBlocksFromEveryEpoch()
    {
        // End-to-end proof of the story criterion: a file whose KeyStore holds THREE epochs
        // (0, 1, 2 after two rotations), with a data block written under each DIFFERENT epoch,
        // is reopened purely from file + password. Decryption is driven by the DEK table loaded
        // FROM DISK (not any in-memory state) and each block is read back through the
        // EncryptedBlockStore layer, which selects the DEK from the block header KeyEpoch.
        const string password = "three epoch rotation password";
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var dek0 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var dek1 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var dek2 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var kek = PasswordKeyDerivation.DeriveKek(password, FastParams, salt);
        var token = KeyVerificationToken.Create(kek);

        var epoch0Plaintext = "written under epoch 0 (original)"u8.ToArray();
        var epoch1Plaintext = "written under epoch 1 (first rotation)"u8.ToArray();
        var epoch2Plaintext = "written under epoch 2 (second rotation)"u8.ToArray();
        long epoch0Offset, epoch1Offset, epoch2Offset;
        BlockLocation activeKeyStore;

        // A KeyStore carrying live DEKs for every epoch up to and including the active one.
        static KeyStoreBlock BuildKeyStore(ushort active, params (ushort Epoch, byte[] Dek)[] deks)
        {
            var ks = new KeyStoreBlock { ActiveEpoch = active };
            foreach (var (epoch, dek) in deks)
                ks.Entries.Add(new KeyStoreEntry { Epoch = epoch, Dek = dek });
            return ks;
        }

        long AppendUnder(BlockManager manager, ushort activeEpoch,
            (ushort Epoch, byte[] Dek)[] table, byte[] plaintext)
        {
            using var provider = new EpochDekProvider(FileId, activeEpoch,
                table.Select(t => new EpochDekProvider.EpochDek(t.Epoch, t.Dek)).ToArray());
            var store = new EncryptedBlockStore(manager, provider);
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintext);
            Ok(appended);
            return appended.Value.Offset;
        }

        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            // Epoch 0: KeyStore with DEK 0, then a data block under epoch 0.
            Ok(EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, BuildKeyStore(0, (0, dek0))));
            epoch0Offset = AppendUnder(manager, 0, new[] { ((ushort)0, dek0) }, epoch0Plaintext);

            // First rotation -> epoch 1: KeyStore now carries DEK 0 + 1, block under epoch 1.
            Ok(EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, BuildKeyStore(1, (0, dek0), (1, dek1))));
            epoch1Offset = AppendUnder(manager, 1, new[] { ((ushort)0, dek0), ((ushort)1, dek1) }, epoch1Plaintext);

            // Second rotation -> epoch 2: KeyStore carries all three DEKs, block under epoch 2.
            var ks2 = EncryptionBootstrap.WriteKeyStore(
                manager, kek, FileId, BuildKeyStore(2, (0, dek0), (1, dek1), (2, dek2)));
            Ok(ks2);
            activeKeyStore = ks2.Value;
            epoch2Offset = AppendUnder(
                manager, 2, new[] { ((ushort)0, dek0), ((ushort)1, dek1), ((ushort)2, dek2) }, epoch2Plaintext);

            Ok(manager.Flush());
        }

        WriteSuperblock(sb =>
        {
            sb.EncryptionEnabled = 1;
            sb.AlgorithmId = EncryptionBootstrap.AesGcmAlgorithmId;
            sb.KdfType = (byte)KdfType.Argon2id;
            sb.KdfParams = FastParams.Pack();
            sb.Salt = salt;
            sb.KeyVerificationToken = token;
            sb.ActiveKeyStoreBlockId = activeKeyStore.BlockId;
            sb.ActiveKeyStoreOffset = activeKeyStore.Offset;
        });

        // --- Reopen with only the file + the password; nothing from the writer survives.
        var superblock = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);

        var opened = EncryptionBootstrap.Open(superblock, password, reopenManager);
        Ok(opened);
        using var provider = opened.Value;

        // The DEK table loaded from disk holds all three epochs; active epoch is the latest.
        Assert.Equal((ushort)2, provider.ActiveEpoch);
        Assert.True(provider.HasLiveDek(0), "epoch 0 DEK must survive two rotations and load");
        Assert.True(provider.HasLiveDek(1), "epoch 1 DEK must survive one rotation and load");
        Assert.True(provider.HasLiveDek(2), "epoch 2 DEK must load");

        // Each block header records the epoch it was sealed under.
        Assert.Equal((ushort)0, reopenManager.Read(epoch0Offset).Value.Header.KeyEpoch);
        Assert.Equal((ushort)1, reopenManager.Read(epoch1Offset).Value.Header.KeyEpoch);
        Assert.Equal((ushort)2, reopenManager.Read(epoch2Offset).Value.Header.KeyEpoch);

        // Reads through EncryptedBlockStore select the DEK per header epoch and recover each payload.
        var store = new EncryptedBlockStore(reopenManager, provider);

        var d0 = store.ReadDecrypted(epoch0Offset);
        Ok(d0);
        Assert.Equal(epoch0Plaintext, d0.Value);

        var d1 = store.ReadDecrypted(epoch1Offset);
        Ok(d1);
        Assert.Equal(epoch1Plaintext, d1.Value);

        var d2 = store.ReadDecrypted(epoch2Offset);
        Ok(d2);
        Assert.Equal(epoch2Plaintext, d2.Value);
    }

    [Fact]
    public void MixedPolicyFile_WithMidLifePolicyChange_RoundTripsBlockByBlockByFlag()
    {
        // End-to-end proof of the story criterion across a close/reopen: a single file carries
        // (i) an encrypted EmailContent block, (ii) a plaintext BTreeLeaf written under Default,
        // and (iii) an ENCRYPTED BTreeLeaf written after the policy changed to Full — so the same
        // BlockType (BTreeLeaf) exists both encrypted and plaintext in one file. After closing and
        // reopening from file + password ONLY (EncryptionBootstrap.Open), every block reads back
        // correctly, decided purely by the per-block Encrypted flag on disk.
        const string password = "mixed policy round trip";
        var emailPlain = "encrypted email body"u8.ToArray();
        var leafDefaultPlain = "plaintext btree leaf (Default policy)"u8.ToArray();
        var leafFullPlain = "encrypted btree leaf (Full policy, mid-life change)"u8.ToArray();
        long emailOffset, leafDefaultOffset, leafFullOffset;

        EncryptionBootstrap.CreatedEncryption created;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;

            // Phase 1 — Default policy: EmailContent encrypts, BTreeLeaf stays plaintext.
            var defaultStore = new EncryptedBlockStore(manager, created.Provider, EncryptionPolicy.Default);
            var a = defaultStore.Append(BlockType.EmailContent, PayloadEncoding.RawBytes, emailPlain);
            var b = defaultStore.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, leafDefaultPlain);
            Ok(a); Ok(b);
            emailOffset = a.Value.Offset;
            leafDefaultOffset = b.Value.Offset;

            // Phase 2 — policy changed to Full: the SAME BTreeLeaf type now encrypts.
            var fullStore = new EncryptedBlockStore(manager, created.Provider, EncryptionPolicy.Full);
            var c = fullStore.Append(BlockType.BTreeLeaf, PayloadEncoding.RawBytes, leafFullPlain);
            Ok(c);
            leafFullOffset = c.Value.Offset;

            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        // --- Reopen with only the file + the password; nothing from the writer survives.
        var superblock = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);

        var opened = EncryptionBootstrap.Open(superblock, password, reopenManager);
        Ok(opened);
        using var provider = opened.Value;

        // On disk: the same BlockType appears both encrypted and plaintext, by flag.
        Assert.True(reopenManager.Read(emailOffset).Value.Header.IsEncrypted);
        Assert.False(reopenManager.Read(leafDefaultOffset).Value.Header.IsEncrypted);
        Assert.True(reopenManager.Read(leafFullOffset).Value.Header.IsEncrypted);
        Assert.Equal(BlockType.BTreeLeaf, reopenManager.Read(leafDefaultOffset).Value.Header.Type);
        Assert.Equal(BlockType.BTreeLeaf, reopenManager.Read(leafFullOffset).Value.Header.Type);

        // The reader's policy (Default — which treats BTreeLeaf as plaintext) DISAGREES with how
        // the second leaf was written. Correct reads of all three prove the read path routes off
        // the per-block header flag, not a policy table.
        var readStore = new EncryptedBlockStore(reopenManager, provider, EncryptionPolicy.Default);
        Assert.Equal(emailPlain, readStore.ReadDecrypted(emailOffset).Value);
        Assert.Equal(leafDefaultPlain, readStore.ReadDecrypted(leafDefaultOffset).Value);
        Assert.Equal(leafFullPlain, readStore.ReadDecrypted(leafFullOffset).Value);
    }

    [Fact]
    public void KdfParams_NonDefault_AreReadFromSuperblockAndHonored()
    {
        const string password = "non-default kdf params";
        // Deliberately different from Argon2idParams.Default (65536 / 3 / 4).
        var customParams = new Argon2idParams(memoryKB: 64, iterations: 2, parallelism: 1);
        Assert.NotEqual(Argon2idParams.Default.MemoryKB, customParams.MemoryKB);

        EncryptionBootstrap.CreatedEncryption created;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, customParams);
            Ok(createdResult);
            created = createdResult.Value;
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        var superblock = LoadSuperblock();

        // The stored parameters round-trip exactly (not silently replaced by defaults).
        var stored = Argon2idParams.Unpack(superblock.KdfParams);
        Assert.Equal(customParams.MemoryKB, stored.MemoryKB);
        Assert.Equal(customParams.Iterations, stored.Iterations);
        Assert.Equal(customParams.Parallelism, stored.Parallelism);

        // Bootstrap opens using the stored (non-default) parameters.
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(superblock, password, reopenManager);
        Ok(opened);
        opened.Value.Dispose();

        // Proof the stored params are load-bearing: deriving with DEFAULT params yields a
        // different KEK that does NOT open the token — only the stored params work.
        var kekDefault = PasswordKeyDerivation.DeriveKek(password, Argon2idParams.Default, superblock.Salt);
        Assert.False(KeyVerificationToken.TryVerify(superblock.KeyVerificationToken, kekDefault),
            "default KDF params must NOT open a file that stored non-default params");
        var kekStored = PasswordKeyDerivation.DeriveKek(password, stored, superblock.Salt);
        Assert.True(KeyVerificationToken.TryVerify(superblock.KeyVerificationToken, kekStored),
            "the stored KDF params must open the token");
    }

    [Fact]
    public void WrongPassword_FailsFast_WithDistinctTokenMismatchError()
    {
        const string password = "the real password";
        EncryptionBootstrap.CreatedEncryption created;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        var superblock = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);

        var opened = EncryptionBootstrap.Open(superblock, "not the password", reopenManager);
        Assert.True(opened.IsFailure);
        var tamper = Assert.IsType<WrongKeyOrTamperError>(opened.VerificationError);
        Assert.Equal(TamperCause.TokenMismatch, tamper.Cause);
    }

    [Fact]
    public void PasswordEncryptionBootstrap_FillsCleanOpenerSeam_AndExposesProvider()
    {
        const string password = "seam password";
        var plaintext = "payload decrypted through the CleanOpener seam"u8.ToArray();
        long dataOffset;

        EncryptionBootstrap.CreatedEncryption created;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;
            var store = new EncryptedBlockStore(manager, created.Provider);
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, plaintext);
            Ok(appended);
            dataOffset = appended.Value.Offset;
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        var superblock = LoadSuperblock();
        using var reopenStream = OpenRW();

        var bootstrap = new PasswordEncryptionBootstrap(reopenStream, password);
        Ok(bootstrap.Bootstrap(superblock));
        Assert.NotNull(bootstrap.Provider);

        using var provider = bootstrap.Provider!;
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);
        var store2 = new EncryptedBlockStore(reopenManager, provider);
        var decrypted = store2.ReadDecrypted(dataOffset);
        Ok(decrypted);
        Assert.Equal(plaintext, decrypted.Value);
    }

    [Fact]
    public void KeyStoreSerializer_RoundTrips_LiveAndRetiredEntries()
    {
        var keyStore = new KeyStoreBlock
        {
            ActiveEpoch = 2,
            Entries =
            {
                new KeyStoreEntry { Epoch = 0, Retired = true },
                new KeyStoreEntry { Epoch = 1, Dek = RandomNumberGenerator.GetBytes(32), CreatedTimestamp = 111 },
                new KeyStoreEntry { Epoch = 2, Dek = RandomNumberGenerator.GetBytes(32), CreatedTimestamp = 222 },
            },
        };

        var bytes = KeyStoreSerializer.Serialize(keyStore);
        var parsed = KeyStoreSerializer.Deserialize(bytes);
        Ok(parsed);

        Assert.Equal((ushort)2, parsed.Value.ActiveEpoch);
        Assert.Equal(3, parsed.Value.Entries.Count);
        var retired = parsed.Value.Entries[0];
        Assert.True(retired.Retired);
        Assert.Empty(retired.Dek);
        Assert.Equal(keyStore.Entries[1].Dek, parsed.Value.Entries[1].Dek);
        Assert.Equal(222, parsed.Value.Entries[2].CreatedTimestamp);

        // A truncated table is rejected, never partially loaded.
        Assert.True(KeyStoreSerializer.Deserialize(bytes.AsSpan(0, bytes.Length - 4)).IsFailure);
    }
}
