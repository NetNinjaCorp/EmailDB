using System.Security.Cryptography;
using EmailDB.Format;
using EmailDB.Format.V3;

namespace EmailDB.UnitTests;

/// <summary>
/// Tests for first-class v3 key rotation (<see cref="EncryptionBootstrap.RotateKey"/> and
/// <see cref="KeyStoreBlock.Rotate"/>, task US-EMDB-79-7, docs/Encryption.md Section 5). Rotation
/// is O(1): it mints a fresh DEK at <c>ActiveEpoch + 1</c>, retains every previous epoch's DEK,
/// appends one re-sealed KeyStore block, and repoints the superblock — no data block is rewritten.
/// Covers:
/// <list type="bullet">
/// <item>rotation activates epoch N+1 and new writes use it;</item>
/// <item>blocks written under an old epoch stay readable after rotation + reopen from disk;</item>
/// <item>each rotation's DEK is fresh (differs from every prior epoch);</item>
/// <item>rotation refuses cleanly at epoch 65535 — a distinct error, and the file is untouched.</item>
/// </list>
/// Uses minimal Argon2id costs so key derivation is fast.
/// </summary>
public class RotateKeyV3EpochBoundTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"emaildb-rotatekey-{Guid.NewGuid():N}.emdb");

    private static readonly byte[] FileId =
        Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray();

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

    // Persists an already-populated superblock (e.g. after RotationOutcome.ApplyTo) to both slots.
    private void PersistSuperblock(Superblock sb)
    {
        using var stream = OpenRW();
        using var sbManager = new SuperblockManager(stream, ownsStream: false);
        Ok(sbManager.Write(sb.Clone()));
        Ok(sbManager.Write(sb.Clone())); // fill both slots
    }

    // Builds an encrypted file whose active epoch is already the ceiling (65535) with one live DEK
    // at that epoch — the starting state for exercising rotation refusal end-to-end. The file opens
    // from file + <paramref name="password"/> alone afterwards.
    private void BuildFileAtMaxEpoch(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var dekMax = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var kek = PasswordKeyDerivation.DeriveKek(password, FastParams, salt);
        var token = KeyVerificationToken.Create(kek);
        BlockLocation activeKeyStore;

        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var ksMax = new KeyStoreBlock
            {
                ActiveEpoch = KeyStoreBlock.MaxEpoch,
                Entries = { new KeyStoreEntry { Epoch = KeyStoreBlock.MaxEpoch, Dek = dekMax } },
            };
            var written = EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, ksMax);
            Ok(written);
            activeKeyStore = written.Value;
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
        CryptographicOperations.ZeroMemory(kek);
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Rotation on a freshly created (epoch 0) file activates epoch 1, and a block appended through
    /// the returned provider is sealed under epoch 1 — proof new writes flow to the new epoch.
    /// </summary>
    [Fact]
    public void RotateKey_ActivatesEpochNPlus1_AndNewWritesUseIt()
    {
        const string password = "rotate to epoch one";
        var newEpochPlaintext = "written under the freshly rotated epoch"u8.ToArray();

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
        var oldKeyStoreOffset = superblock.ActiveKeyStoreOffset;

        long newEpochOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var rotated = EncryptionBootstrap.RotateKey(superblock, password, manager);
            Ok(rotated);
            Assert.Equal((ushort)1, rotated.Value.NewEpoch);
            using var provider = rotated.Value.Provider;
            Assert.Equal((ushort)1, provider.ActiveEpoch);

            // The rotation appended a NEW KeyStore block; its pointer differs from the old one.
            Assert.NotEqual(oldKeyStoreOffset, rotated.Value.KeyStore.Offset);

            // A new write goes out under epoch 1.
            var store = new EncryptedBlockStore(manager, provider);
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, newEpochPlaintext);
            Ok(appended);
            newEpochOffset = appended.Value.Offset;
            Ok(manager.Flush());

            Assert.Equal((ushort)1, manager.Read(newEpochOffset).Value.Header.KeyEpoch);

            rotated.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);

        // Reopen from file + password only: the active epoch is now 1.
        var reloaded = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(reloaded, password, reopenManager);
        Ok(opened);
        using var reopened = opened.Value;
        Assert.Equal((ushort)1, reopened.ActiveEpoch);

        var readStore = new EncryptedBlockStore(reopenManager, reopened);
        var decrypted = readStore.ReadDecrypted(newEpochOffset);
        Ok(decrypted);
        Assert.Equal(newEpochPlaintext, decrypted.Value);
    }

    /// <summary>
    /// A block written under epoch 0 is still decryptable after rotating to epoch 1 and reopening
    /// the file from disk — the old epoch's DEK is retained in the re-sealed KeyStore.
    /// </summary>
    [Fact]
    public void OldEpochBlocks_RemainReadable_AfterRotationAndReopen()
    {
        const string password = "old epoch survives rotation";
        var oldPlaintext = "written before rotation (epoch 0)"u8.ToArray();
        var newPlaintext = "written after rotation (epoch 1)"u8.ToArray();

        EncryptionBootstrap.CreatedEncryption created;
        long oldEpochOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;

            var store = new EncryptedBlockStore(manager, created.Provider);
            var a = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, oldPlaintext);
            Ok(a);
            oldEpochOffset = a.Value.Offset;
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        // Rotate, then write a new-epoch block, then commit the superblock.
        var superblock = LoadSuperblock();
        long newEpochOffset;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var rotated = EncryptionBootstrap.RotateKey(superblock, password, manager);
            Ok(rotated);
            using var provider = rotated.Value.Provider;

            var store = new EncryptedBlockStore(manager, provider);
            var b = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, newPlaintext);
            Ok(b);
            newEpochOffset = b.Value.Offset;
            Ok(manager.Flush());

            rotated.Value.ApplyTo(superblock);
        }
        PersistSuperblock(superblock);

        // Reopen from disk: both the old-epoch and new-epoch blocks decrypt.
        var reloaded = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(reloaded, password, reopenManager);
        Ok(opened);
        using var provider2 = opened.Value;

        Assert.Equal((ushort)1, provider2.ActiveEpoch);
        Assert.True(provider2.HasLiveDek(0), "epoch 0 DEK must survive rotation and load");
        Assert.True(provider2.HasLiveDek(1), "epoch 1 DEK must load");
        Assert.Equal((ushort)0, reopenManager.Read(oldEpochOffset).Value.Header.KeyEpoch);
        Assert.Equal((ushort)1, reopenManager.Read(newEpochOffset).Value.Header.KeyEpoch);

        var readStore = new EncryptedBlockStore(reopenManager, provider2);
        var oldDecrypted = readStore.ReadDecrypted(oldEpochOffset);
        Ok(oldDecrypted);
        Assert.Equal(oldPlaintext, oldDecrypted.Value);
        var newDecrypted = readStore.ReadDecrypted(newEpochOffset);
        Ok(newDecrypted);
        Assert.Equal(newPlaintext, newDecrypted.Value);
    }

    /// <summary>
    /// The whole-criterion end-to-end proof: rotating a live file three times marches the active
    /// epoch 0→1→2→3 one step at a time (never a skip, never N+2), the reloaded-from-disk superblock
    /// resolves to exactly that new epoch after each rotation, a block written after each rotation is
    /// stamped with the epoch active at that moment, and — after a final reopen from file+password
    /// only — a block written under <em>every</em> prior epoch (0, 1, 2 and 3) still decrypts to its
    /// original plaintext. This exercises "old-epoch blocks remain readable" across MULTIPLE
    /// rotations, not just the first, which the single-rotation tests above do not.
    /// </summary>
    [Fact]
    public void MultipleRotations_EveryPriorEpochBlock_RemainsReadable_AfterReopenFromPassword()
    {
        const string password = "three rotations, four live epochs";
        const int rotations = 3; // final active epoch = 3

        // epoch -> (block offset, plaintext) written while that epoch was active.
        var byEpoch = new Dictionary<ushort, (long Offset, byte[] Plaintext)>();
        byte[] PlaintextFor(ushort epoch) =>
            System.Text.Encoding.UTF8.GetBytes($"payload written under epoch {epoch}");

        // Create the file at epoch 0 and write one epoch-0 block.
        EncryptionBootstrap.CreatedEncryption created;
        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            var createdResult = EncryptionBootstrap.CreateEncryption(manager, FileId, password, FastParams);
            Ok(createdResult);
            created = createdResult.Value;

            var store = new EncryptedBlockStore(manager, created.Provider);
            var p0 = PlaintextFor(0);
            var a = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, p0);
            Ok(a);
            Assert.Equal((ushort)0, manager.Read(a.Value.Offset).Value.Header.KeyEpoch);
            byEpoch[0] = (a.Value.Offset, p0);
            Ok(manager.Flush());
            created.Provider.Dispose();
        }
        WriteSuperblock(created.ApplyTo);

        // Rotate N times. Each rotation must advance the active epoch by EXACTLY one; a block written
        // right after is stamped with the new epoch; the reloaded superblock resolves to the new epoch.
        for (ushort expected = 1; expected <= rotations; expected++)
        {
            var superblock = LoadSuperblock();

            using (var stream = OpenRW())
            using (var manager = new BlockManager(
                stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
            {
                var rotated = EncryptionBootstrap.RotateKey(superblock, password, manager);
                Ok(rotated);
                // (a) exactly N+1 — never a skip, never N+2.
                Assert.Equal(expected, rotated.Value.NewEpoch);
                using var provider = rotated.Value.Provider;
                Assert.Equal(expected, provider.ActiveEpoch);

                // (d) a new write is stamped with the freshly activated epoch.
                var store = new EncryptedBlockStore(manager, provider);
                var pn = PlaintextFor(expected);
                var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, pn);
                Ok(appended);
                Assert.Equal(expected, manager.Read(appended.Value.Offset).Value.Header.KeyEpoch);
                byEpoch[expected] = (appended.Value.Offset, pn);
                Ok(manager.Flush());

                rotated.Value.ApplyTo(superblock);
            }
            PersistSuperblock(superblock);

            // (a) after a disk round-trip, the superblock's KeyStore pointer resolves to epoch N+1.
            var reloaded = LoadSuperblock();
            using var checkStream = OpenRW();
            using var checkManager = new BlockManager(
                checkStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
            var checkOpen = EncryptionBootstrap.Open(reloaded, password, checkManager);
            Ok(checkOpen);
            using var checkProvider = checkOpen.Value;
            Assert.Equal(expected, checkProvider.ActiveEpoch);
        }

        // (b) Final reopen from file + password ONLY: every epoch 0..3 has a live DEK, every block
        // still carries its original epoch stamp, and every block decrypts to its original plaintext.
        var finalSb = LoadSuperblock();
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: finalSb.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(finalSb, password, reopenManager);
        Ok(opened);
        using var reopened = opened.Value;
        Assert.Equal((ushort)rotations, reopened.ActiveEpoch);

        var readStore = new EncryptedBlockStore(reopenManager, reopened);
        for (ushort epoch = 0; epoch <= rotations; epoch++)
        {
            Assert.True(reopened.HasLiveDek(epoch), $"epoch {epoch} DEK must survive {rotations} rotations");
            var (offset, plaintext) = byEpoch[epoch];
            Assert.Equal(epoch, reopenManager.Read(offset).Value.Header.KeyEpoch);
            var decrypted = readStore.ReadDecrypted(offset);
            Ok(decrypted);
            Assert.Equal(plaintext, decrypted.Value);
        }
    }

    /// <summary>
    /// Rotation RETAINS every prior epoch's DEK byte-for-byte — it is carried across unchanged, never
    /// re-derived. Snapshots each epoch's DEK bytes before rotating, then after every one of eight
    /// rotations asserts each pre-existing entry's <see cref="KeyStoreEntry.Dek"/> is byte-identical
    /// to its snapshot (and its epoch/retired flag unchanged). This is what makes old-epoch blocks
    /// stay decryptable; a re-derived or reshuffled prior DEK would silently break them.
    /// </summary>
    [Fact]
    public void Rotate_RetainsPriorEpochDeks_ByteIdenticalAcrossRotations()
    {
        var keyStore = new KeyStoreBlock
        {
            ActiveEpoch = 0,
            Entries = { new KeyStoreEntry { Epoch = 0, Dek = RandomNumberGenerator.GetBytes(32) } },
        };

        // Immutable snapshot of every epoch's DEK bytes as they were first minted.
        var snapshot = new Dictionary<ushort, byte[]> { [0] = (byte[])keyStore.Entries[0].Dek.Clone() };

        for (ushort i = 1; i <= 8; i++)
        {
            var minted = keyStore.Rotate();
            snapshot[minted.Epoch] = (byte[])minted.Dek.Clone();

            // Every prior epoch's stored entry is byte-identical to when it was minted — retained,
            // not re-derived — and its epoch/retired flag is untouched.
            foreach (var (epoch, expectedDek) in snapshot)
            {
                var entry = keyStore.Entries.Single(e => e.Epoch == epoch);
                Assert.False(entry.Retired, $"epoch {epoch} must stay live across rotation to {i}");
                Assert.True(expectedDek.AsSpan().SequenceEqual(entry.Dek),
                    $"epoch {epoch}'s DEK must be byte-identical after rotating to epoch {i} (retained, not re-derived)");
            }
        }
    }

    /// <summary>
    /// Each rotation mints a DEK that differs from every prior epoch's DEK (CSPRNG freshness),
    /// exercised at the model layer where the key bytes are visible.
    /// </summary>
    [Fact]
    public void Rotate_MintsFreshDek_DistinctFromAllPriorEpochs()
    {
        var keyStore = new KeyStoreBlock
        {
            ActiveEpoch = 0,
            Entries = { new KeyStoreEntry { Epoch = 0, Dek = RandomNumberGenerator.GetBytes(32) } },
        };

        var dekHistory = new List<byte[]> { (byte[])keyStore.Entries[0].Dek.Clone() };
        for (ushort i = 1; i <= 8; i++)
        {
            var entry = keyStore.Rotate();
            Assert.Equal(i, entry.Epoch);
            Assert.Equal(i, keyStore.ActiveEpoch);
            Assert.Equal(32, entry.Dek.Length);
            Assert.False(entry.Retired);

            foreach (var prior in dekHistory)
                Assert.False(prior.AsSpan().SequenceEqual(entry.Dek),
                    $"rotated DEK for epoch {i} must differ from every prior epoch's DEK");
            dekHistory.Add((byte[])entry.Dek.Clone());
        }

        // Every prior epoch is retained (not retired) and its DEK is unchanged.
        Assert.Equal(9, keyStore.Entries.Count);
        Assert.All(keyStore.Entries, e => Assert.False(e.Retired));
    }

    /// <summary>
    /// The model-layer bound: rotating a table already at epoch 65535 throws the distinct
    /// <see cref="EpochExhaustedError"/> and leaves the table unmutated (never wraps to 0).
    /// </summary>
    [Fact]
    public void Rotate_AtMaxEpoch_ThrowsEpochExhausted_AndLeavesTableUnchanged()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var keyStore = new KeyStoreBlock
        {
            ActiveEpoch = KeyStoreBlock.MaxEpoch,
            Entries = { new KeyStoreEntry { Epoch = KeyStoreBlock.MaxEpoch, Dek = dek } },
        };

        var ex = Assert.Throws<EpochExhaustedError>(() => keyStore.Rotate());
        Assert.Equal(KeyStoreBlock.MaxEpoch, ex.ActiveEpoch);

        // No wrap, no new entry: the table is exactly as it was.
        Assert.Equal(KeyStoreBlock.MaxEpoch, keyStore.ActiveEpoch);
        Assert.Single(keyStore.Entries);
        Assert.Equal(KeyStoreBlock.MaxEpoch, keyStore.Entries[0].Epoch);
    }

    /// <summary>
    /// End-to-end epoch ceiling: <see cref="EncryptionBootstrap.RotateKey"/> on a file whose active
    /// epoch is 65535 returns a clean failure carrying an <see cref="EpochExhaustedError"/> message,
    /// writes nothing (file length unchanged, superblock pointer unchanged), and the file still
    /// reopens at epoch 65535 — proof the refusal never wrapped or corrupted anything.
    /// </summary>
    [Fact]
    public void RotateKey_FailsCleanly_AtEpoch65535_WithNoStateChange()
    {
        const string password = "at the epoch ceiling";
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var dekMax = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var kek = PasswordKeyDerivation.DeriveKek(password, FastParams, salt);
        var token = KeyVerificationToken.Create(kek);
        BlockLocation activeKeyStore;

        using (var stream = OpenRW())
        using (var manager = new BlockManager(stream, ownsStream: false))
        {
            // A valid, minimal KeyStore whose active epoch is already the ceiling.
            var ksMax = new KeyStoreBlock
            {
                ActiveEpoch = KeyStoreBlock.MaxEpoch,
                Entries = { new KeyStoreEntry { Epoch = KeyStoreBlock.MaxEpoch, Dek = dekMax } },
            };
            var written = EncryptionBootstrap.WriteKeyStore(manager, kek, FileId, ksMax);
            Ok(written);
            activeKeyStore = written.Value;
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

        var superblock = LoadSuperblock();
        long lengthBefore = new FileInfo(_path).Length;
        long pointerBefore = superblock.ActiveKeyStoreOffset;

        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var rotated = EncryptionBootstrap.RotateKey(superblock, password, manager);
            Assert.True(rotated.IsFailure, "rotation at epoch 65535 must be refused");
            Assert.Contains("65535", rotated.Error);
            // The in-memory superblock object was not repointed.
            Assert.Equal(pointerBefore, superblock.ActiveKeyStoreOffset);
        }

        // No bytes were appended by the refused rotation.
        Assert.Equal(lengthBefore, new FileInfo(_path).Length);

        // The file still opens, still at the ceiling epoch — nothing wrapped or broke.
        var reloaded = LoadSuperblock();
        Assert.Equal(pointerBefore, reloaded.ActiveKeyStoreOffset);
        using var reopenStream = OpenRW();
        using var reopenManager = new BlockManager(
            reopenStream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false);
        var opened = EncryptionBootstrap.Open(reloaded, password, reopenManager);
        Ok(opened);
        using var provider = opened.Value;
        Assert.Equal(KeyStoreBlock.MaxEpoch, provider.ActiveEpoch);
        Assert.True(provider.HasLiveDek(KeyStoreBlock.MaxEpoch));
    }

    /// <summary>
    /// The boundary is exactly 65535, not one short of it (off-by-one guard): rotating a table at
    /// <c>MaxEpoch - 1</c> (65534) SUCCEEDS and activates the ceiling epoch 65535 — the last legal
    /// rotation — while a second rotation from 65535 is refused with the distinct
    /// <see cref="EpochExhaustedError"/> and no wrap. A fence-post bug that refused at 65534 (or
    /// wrapped at 65535) would be caught here.
    /// </summary>
    [Fact]
    public void Rotate_At65534_SucceedsTo65535_ThenRefusesAt65535()
    {
        const ushort penultimate = KeyStoreBlock.MaxEpoch - 1; // 65534
        var keyStore = new KeyStoreBlock
        {
            ActiveEpoch = penultimate,
            Entries = { new KeyStoreEntry { Epoch = penultimate, Dek = RandomNumberGenerator.GetBytes(32) } },
        };

        // 65534 -> 65535 is the LAST legal rotation: it mints a fresh live DEK at the ceiling epoch.
        var minted = keyStore.Rotate();
        Assert.Equal(KeyStoreBlock.MaxEpoch, minted.Epoch);
        Assert.Equal(KeyStoreBlock.MaxEpoch, keyStore.ActiveEpoch);
        Assert.False(minted.Retired);
        Assert.Equal(32, minted.Dek.Length);
        Assert.Equal(2, keyStore.Entries.Count);

        // One step past the ceiling is refused — proving the bound sits at 65535, not 65534.
        var ex = Assert.Throws<EpochExhaustedError>(() => keyStore.Rotate());
        Assert.Equal(KeyStoreBlock.MaxEpoch, ex.ActiveEpoch);
        Assert.Equal(KeyStoreBlock.MaxEpoch, keyStore.ActiveEpoch); // no wrap to 0
        Assert.Equal(2, keyStore.Entries.Count);                    // no new entry
    }

    /// <summary>
    /// "Fails cleanly" also means "still fully operational": after a rotation is refused at epoch
    /// 65535, the file still (a) accepts a NEW encrypted write — stamped at the ceiling epoch — that
    /// reads back after a reopen, and (b) permits a password change (which consumes no epoch and is
    /// therefore not blocked by the ceiling). After the change, the file reopens under the NEW
    /// password still at epoch 65535, the epoch-65535 block still decrypts, the old password no
    /// longer opens, and rotation is STILL refused — the ceiling is a property of the file, not the
    /// credential.
    /// </summary>
    [Fact]
    public void AtEpoch65535_AfterRefusedRotation_FileStillAcceptsWritesAndPasswordChange()
    {
        const string password = "operational at the ceiling";
        const string newPassword = "rekeyed at the ceiling";
        var newPlaintext = "written at epoch 65535 after a refused rotation"u8.ToArray();

        BuildFileAtMaxEpoch(password);

        // Refuse a rotation at the ceiling (the behaviour under test), then prove the file is unharmed.
        var superblock = LoadSuperblock();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false))
        {
            var rotated = EncryptionBootstrap.RotateKey(superblock, password, manager);
            Assert.True(rotated.IsFailure, "rotation at epoch 65535 must be refused");
            Assert.Contains("65535", rotated.Error);
        }

        // (a) A new write still lands, stamped at the ceiling epoch.
        long writeOffset;
        var sbForWrite = LoadSuperblock();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: sbForWrite.MaxPayloadLength, ownsStream: false))
        {
            var opened = EncryptionBootstrap.Open(sbForWrite, password, manager);
            Ok(opened);
            using var provider = opened.Value;
            Assert.Equal(KeyStoreBlock.MaxEpoch, provider.ActiveEpoch);

            var store = new EncryptedBlockStore(manager, provider);
            var appended = store.AppendEncrypted(BlockType.EmailContent, PayloadEncoding.RawBytes, newPlaintext);
            Ok(appended);
            writeOffset = appended.Value.Offset;
            Ok(manager.Flush());
            Assert.Equal(KeyStoreBlock.MaxEpoch, manager.Read(writeOffset).Value.Header.KeyEpoch);
        }

        // (b) Password change succeeds at the ceiling — it consumes NO epoch, so nothing blocks it.
        var sbForPwd = LoadSuperblock();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: sbForPwd.MaxPayloadLength, ownsStream: false))
        {
            var changed = EncryptionBootstrap.ChangePassword(sbForPwd, password, newPassword, manager, FastParams);
            Ok(changed);
            using var provider = changed.Value.Provider;
            Assert.Equal(KeyStoreBlock.MaxEpoch, provider.ActiveEpoch); // active epoch is preserved
            Ok(manager.Flush());
            changed.Value.ApplyTo(sbForPwd);
        }
        PersistSuperblock(sbForPwd);

        // Reopen with the NEW password: still at the ceiling, the epoch-65535 block still decrypts,
        // the old password is rejected, and a rotation is STILL refused.
        var reloaded = LoadSuperblock();
        using (var stream = OpenRW())
        using (var manager = new BlockManager(
            stream, maxPayloadLength: reloaded.MaxPayloadLength, ownsStream: false))
        {
            var opened = EncryptionBootstrap.Open(reloaded, newPassword, manager);
            Ok(opened);
            using var provider = opened.Value;
            Assert.Equal(KeyStoreBlock.MaxEpoch, provider.ActiveEpoch);
            Assert.True(provider.HasLiveDek(KeyStoreBlock.MaxEpoch));

            var store = new EncryptedBlockStore(manager, provider);
            var decrypted = store.ReadDecrypted(writeOffset);
            Ok(decrypted);
            Assert.Equal(newPlaintext, decrypted.Value);

            // The ceiling survives the password change: rotation is still refused.
            var rotatedAgain = EncryptionBootstrap.RotateKey(reloaded, newPassword, manager);
            Assert.True(rotatedAgain.IsFailure, "rotation must still be refused at the ceiling after a password change");
            Assert.Contains("65535", rotatedAgain.Error);

            // The OLD password no longer opens the file.
            var openedOld = EncryptionBootstrap.Open(reloaded, password, manager);
            Assert.True(openedOld.IsFailure, "old password must no longer open after the change");
        }
    }
}
