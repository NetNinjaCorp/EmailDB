using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// The open-time encryption bootstrap (docs/Encryption.md Section 2, spec Section 10.2 step 2):
/// the chain that turns a file's superblock fields plus a password into a live
/// <see cref="EpochDekProvider"/>, deriving every key entirely from the file — never from
/// compiled-in constants. This is the production path that v1 never built (US-EMDB-76):
///
/// <list type="number">
///   <item>Read the superblock encryption fields (<c>KdfType</c>, <c>KdfParams</c>, <c>Salt</c>,
///   <c>KeyVerificationToken</c>, <c>ActiveKeyStore</c> pointer).</item>
///   <item>Derive the KEK with Argon2id, honoring the <em>stored</em> parameters and salt
///   (<see cref="PasswordKeyDerivation"/>) — a file with non-default costs still opens.</item>
///   <item>Fast-fail the <see cref="KeyVerificationToken"/>: a mismatch is wrong-password
///   <em>or</em> tampered KDF fields, surfaced before the KeyStore is touched.</item>
///   <item>Read the KeyStore block and decrypt its payload with the KEK
///   (<see cref="AesGcmBlockCipher"/>, AAD-bound to FileId/BlockId/type).</item>
///   <item>Deserialize the DEK table (<see cref="KeyStoreSerializer"/>) and build the
///   <see cref="EpochDekProvider"/> — which owns and zeroizes the KEK and DEKs on dispose.</item>
/// </list>
///
/// <para>The reverse direction (<see cref="CreateEncryption"/> / <see cref="WriteKeyStore"/>)
/// exists so a new encrypted file can be created and round-tripped: generate the salt and first
/// DEK, write the KEK-encrypted KeyStore block, and hand back the superblock fields to persist.</para>
/// </summary>
public static class EncryptionBootstrap
{
    /// <summary>Superblock <c>AlgorithmId</c> value for AES-256-GCM (spec Section 3.1).</summary>
    public const byte AesGcmAlgorithmId = 1;

    /// <summary>
    /// Runs the full open-time bootstrap for an encrypted file, returning a live provider built
    /// from the file's DEK table. The caller scopes the provider in a <c>using</c> so its key
    /// material is zeroized. Every failure mode is a <see cref="Result{T}"/> failure — a wrong
    /// password / tampered KDF field carries the distinct <see cref="WrongKeyOrTamperError"/> via
    /// <see cref="Result{T}.VerificationError"/>.
    /// </summary>
    /// <param name="superblock">The selected superblock carrying the encryption fields.</param>
    /// <param name="password">The user password (NFC-normalized internally before the KDF).</param>
    /// <param name="blockManager">A manager over the same file, used to read the KeyStore block.</param>
    public static Result<EpochDekProvider> Open(
        Superblock superblock, string password, BlockManager blockManager)
    {
        ArgumentNullException.ThrowIfNull(superblock);
        ArgumentNullException.ThrowIfNull(blockManager);

        if (superblock.EncryptionEnabled == 0)
            return Result<EpochDekProvider>.Failure(
                "Superblock marks the file as plaintext (EncryptionEnabled = 0); no encryption bootstrap to run.");
        if (superblock.AlgorithmId != AesGcmAlgorithmId)
            return Result<EpochDekProvider>.Failure(
                $"Unsupported encryption AlgorithmId {superblock.AlgorithmId}; this build only supports AES-256-GCM ({AesGcmAlgorithmId}).");

        // 2. Derive the KEK from the STORED KDF params + salt (never compiled-in constants).
        byte[] kek;
        try
        {
            kek = PasswordKeyDerivation.DeriveKek(
                password, superblock.KdfType, superblock.KdfParams, superblock.Salt);
        }
        catch (NotSupportedException ex)
        {
            return Result<EpochDekProvider>.Failure($"Encryption bootstrap failed deriving the KEK: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Result<EpochDekProvider>.Failure($"Encryption bootstrap failed deriving the KEK: {ex.Message}");
        }

        bool kekOwnedByProvider = false;
        try
        {
            // 3. Fast-fail the KeyVerificationToken: distinct wrong-password / tamper signal.
            try
            {
                KeyVerificationToken.Verify(superblock.KeyVerificationToken, kek);
            }
            catch (WrongKeyOrTamperError err)
            {
                return Result<EpochDekProvider>.Failure(err);
            }
            catch (ArgumentException ex)
            {
                return Result<EpochDekProvider>.Failure(
                    $"Encryption bootstrap failed verifying the token: {ex.Message}");
            }

            // 4. Read the KeyStore block and decrypt its payload with the KEK.
            var keyStoreRes = ReadAndDecryptKeyStore(superblock, kek, blockManager);
            if (keyStoreRes.IsFailure)
                return keyStoreRes.VerificationError is not null
                    ? Result<EpochDekProvider>.Failure(keyStoreRes.VerificationError)
                    : Result<EpochDekProvider>.Failure(keyStoreRes.Error);

            var keyStore = keyStoreRes.Value;
            try
            {
                // 5. Build the provider from the DEK table. It copies the KEK and every DEK and
                //    zeroizes them on dispose, so we transfer KEK ownership to it here.
                var provider = new EpochDekProvider(
                    superblock.FileId, keyStore.ActiveEpoch, keyStore.ToProviderEntries(), kek);
                kekOwnedByProvider = true;
                return Result<EpochDekProvider>.Success(provider);
            }
            catch (ArgumentException ex)
            {
                // e.g. active epoch has no live DEK, or a malformed table.
                return Result<EpochDekProvider>.Failure(
                    $"Encryption bootstrap failed building the provider: {ex.Message}");
            }
            finally
            {
                // The provider took its own copies; scrub the transient plaintext DEK bytes.
                foreach (var entry in keyStore.Entries)
                    CryptographicOperations.ZeroMemory(entry.Dek);
            }
        }
        finally
        {
            // The provider copies the KEK, so our buffer is always scrubbed here regardless.
            CryptographicOperations.ZeroMemory(kek);
            _ = kekOwnedByProvider; // documented: provider holds its own zeroized copy.
        }
    }

    /// <summary>
    /// The result of a successful <see cref="RotateKey"/>: the new KeyStore block's location (the
    /// superblock's next pointer), the newly activated epoch, and a live provider whose
    /// <see cref="EpochDekProvider.ActiveEpoch"/> is that new epoch. The provider owns and zeroizes
    /// the KEK and every DEK on dispose, exactly like the one <see cref="Open"/> returns.
    /// </summary>
    public sealed class RotationOutcome
    {
        /// <summary>Location of the freshly written KeyStore block (the superblock's new pointer).</summary>
        public required BlockLocation KeyStore { get; init; }

        /// <summary>The epoch new blocks are now encrypted under (the previous active epoch + 1).</summary>
        public required ushort NewEpoch { get; init; }

        /// <summary>A live provider at the new epoch; the caller scopes it in a <c>using</c>.</summary>
        public required EpochDekProvider Provider { get; init; }

        /// <summary>
        /// Repoints <paramref name="superblock"/> at the new KeyStore block (its BlockId + offset).
        /// This is the rotation's commit point: persisting the superblock afterward makes the new
        /// epoch active on disk; a crash before it leaves the old KeyStore — and the old active
        /// epoch — fully in force (docs/Encryption.md Section 5, "append … then superblock update").
        /// </summary>
        public void ApplyTo(Superblock superblock)
        {
            ArgumentNullException.ThrowIfNull(superblock);
            superblock.ActiveKeyStoreBlockId = (byte[])KeyStore.BlockId.Clone();
            superblock.ActiveKeyStoreOffset = KeyStore.Offset;
        }
    }

    /// <summary>
    /// Key rotation (docs/Encryption.md Section 5, "Key rotation — O(1)"): mint a fresh DEK at
    /// <c>ActiveEpoch + 1</c>, retain every previous epoch's DEK, and append a re-sealed KeyStore
    /// block carrying the whole table. No data block is touched, so blocks written under an older
    /// epoch keep their header <c>KeyEpoch</c> and stay decryptable; only <em>new</em> writes use
    /// the new epoch. The caller stamps the returned <see cref="RotationOutcome.ApplyTo"/> onto the
    /// superblock and persists it to commit the rotation.
    ///
    /// <para>Refuses cleanly at the epoch ceiling: when the active epoch is already
    /// <see cref="KeyStoreBlock.MaxEpoch"/> (65535) the method returns a failure carrying an
    /// <see cref="EpochExhaustedError"/> <b>before writing anything</b> — the file is unchanged and
    /// the old epoch stays active (never wraps to 0). Also fast-fails a wrong password / tampered
    /// KDF field via the same distinct <see cref="WrongKeyOrTamperError"/> as <see cref="Open"/>.</para>
    /// </summary>
    /// <param name="superblock">The current superblock carrying the encryption fields and KeyStore pointer.</param>
    /// <param name="password">The user password (NFC-normalized internally before the KDF).</param>
    /// <param name="blockManager">A manager over the same file: reads the current KeyStore, appends the new one.</param>
    public static Result<RotationOutcome> RotateKey(
        Superblock superblock, string password, BlockManager blockManager)
    {
        ArgumentNullException.ThrowIfNull(superblock);
        ArgumentNullException.ThrowIfNull(blockManager);

        if (superblock.EncryptionEnabled == 0)
            return Result<RotationOutcome>.Failure(
                "Superblock marks the file as plaintext (EncryptionEnabled = 0); there is no key to rotate.");
        if (superblock.AlgorithmId != AesGcmAlgorithmId)
            return Result<RotationOutcome>.Failure(
                $"Unsupported encryption AlgorithmId {superblock.AlgorithmId}; this build only supports AES-256-GCM ({AesGcmAlgorithmId}).");

        // Re-derive the KEK from the STORED KDF params + salt (never compiled-in constants). This
        // also re-verifies the password below before any write, so a rotation on the wrong password
        // fails cleanly with no state change.
        byte[] kek;
        try
        {
            kek = PasswordKeyDerivation.DeriveKek(
                password, superblock.KdfType, superblock.KdfParams, superblock.Salt);
        }
        catch (NotSupportedException ex)
        {
            return Result<RotationOutcome>.Failure($"Key rotation failed deriving the KEK: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Result<RotationOutcome>.Failure($"Key rotation failed deriving the KEK: {ex.Message}");
        }

        bool kekOwnedByProvider = false;
        try
        {
            try
            {
                KeyVerificationToken.Verify(superblock.KeyVerificationToken, kek);
            }
            catch (WrongKeyOrTamperError err)
            {
                return Result<RotationOutcome>.Failure(err);
            }
            catch (ArgumentException ex)
            {
                return Result<RotationOutcome>.Failure($"Key rotation failed verifying the token: {ex.Message}");
            }

            var keyStoreRes = ReadAndDecryptKeyStore(superblock, kek, blockManager);
            if (keyStoreRes.IsFailure)
                return keyStoreRes.VerificationError is not null
                    ? Result<RotationOutcome>.Failure(keyStoreRes.VerificationError)
                    : Result<RotationOutcome>.Failure(keyStoreRes.Error);

            var keyStore = keyStoreRes.Value;
            try
            {
                // Advance the in-memory table by one epoch. Rotate() refuses at MaxEpoch BEFORE
                // mutating, so the epoch-ceiling path performs no write — the file is unchanged.
                ushort newEpoch;
                try
                {
                    keyStore.Rotate();
                    newEpoch = keyStore.ActiveEpoch;
                }
                catch (EpochExhaustedError ex)
                {
                    return Result<RotationOutcome>.Failure(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return Result<RotationOutcome>.Failure($"Key rotation refused: {ex.Message}");
                }

                var written = WriteKeyStore(blockManager, kek, superblock.FileId, keyStore);
                if (written.IsFailure)
                    return Result<RotationOutcome>.Failure($"Key rotation failed writing the KeyStore: {written.Error}");

                var provider = new EpochDekProvider(
                    superblock.FileId, newEpoch, keyStore.ToProviderEntries(), kek);
                kekOwnedByProvider = true;
                return Result<RotationOutcome>.Success(new RotationOutcome
                {
                    KeyStore = written.Value,
                    NewEpoch = newEpoch,
                    Provider = provider,
                });
            }
            catch (ArgumentException ex)
            {
                return Result<RotationOutcome>.Failure($"Key rotation failed building the provider: {ex.Message}");
            }
            finally
            {
                // The provider took its own copies of every DEK; scrub the transient plaintext bytes.
                foreach (var entry in keyStore.Entries)
                    CryptographicOperations.ZeroMemory(entry.Dek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            _ = kekOwnedByProvider; // documented: provider holds its own zeroized KEK copy.
        }
    }

    /// <summary>
    /// The result of a successful <see cref="ChangePassword"/>: the new superblock encryption
    /// fields (fresh Salt, possibly-upgraded KdfParams, and a token that only the NEW password
    /// opens), the location of the KeyStore block re-sealed under the new KEK, and a live provider
    /// at the file's existing active epoch. The provider owns and zeroizes the new KEK and every DEK
    /// on dispose, exactly like the one <see cref="Open"/> returns.
    /// </summary>
    public sealed class PasswordChangeOutcome
    {
        /// <summary>Fresh CSPRNG salt for the new password's KDF (superblock <c>Salt</c>).</summary>
        public required byte[] Salt { get; init; }

        /// <summary>Packed Argon2id parameters used for the new KEK (superblock <c>KdfParams</c>).</summary>
        public required byte[] KdfParams { get; init; }

        /// <summary>The <c>KeyVerificationToken</c> sealed under the new KEK (superblock field).</summary>
        public required byte[] KeyVerificationToken { get; init; }

        /// <summary>Location of the KeyStore block re-sealed under the new KEK (superblock pointer).</summary>
        public required BlockLocation KeyStore { get; init; }

        /// <summary>A live provider at the file's active epoch, keyed by the new KEK.</summary>
        public required EpochDekProvider Provider { get; init; }

        /// <summary>
        /// Stamps the new credential fields onto <paramref name="superblock"/>: the fresh Salt, the
        /// (possibly upgraded) KdfParams, the new-KEK KeyVerificationToken, and the pointer to the
        /// re-sealed KeyStore block. This single superblock write is the password change's commit
        /// point (docs/Encryption.md Section 5): persisting the superblock afterward makes the new
        /// password active on disk; a crash before it leaves the OLD Salt/KdfParams/token/pointer —
        /// and therefore the old password — fully in force. No data block is touched.
        /// </summary>
        public void ApplyTo(Superblock superblock)
        {
            ArgumentNullException.ThrowIfNull(superblock);
            superblock.Salt = (byte[])Salt.Clone();
            superblock.KdfParams = (byte[])KdfParams.Clone();
            superblock.KeyVerificationToken = (byte[])KeyVerificationToken.Clone();
            superblock.ActiveKeyStoreBlockId = (byte[])KeyStore.BlockId.Clone();
            superblock.ActiveKeyStoreOffset = KeyStore.Offset;
        }
    }

    /// <summary>
    /// Password change (docs/Encryption.md Section 5, "Password change — O(1)"): re-wraps the DEK
    /// table under a KEK derived from the new password, without touching a single data block. The
    /// DEKs themselves never change, so every block — of every epoch — stays decryptable; only the
    /// KEK that seals the KeyStore, and the KDF inputs that derive it, change.
    ///
    /// <para>The sequence: verify the OLD password against the stored token → decrypt the KeyStore
    /// with the old KEK → mint a fresh CSPRNG Salt (and optionally upgrade the Argon2id parameters
    /// via <paramref name="newParameters"/>) → derive the NEW KEK from the new password → create a
    /// new <see cref="KeyVerificationToken"/> → append the same DEK table re-sealed under the new
    /// KEK as one KeyStore block. The caller stamps the returned
    /// <see cref="PasswordChangeOutcome.ApplyTo"/> onto the superblock and persists it; that
    /// superblock write is the commit point. A crash before it leaves the old Salt/KdfParams/token
    /// and KeyStore pointer intact, so the old password still opens the file and the new one is
    /// rejected — the change is atomic at the superblock boundary.</para>
    ///
    /// <para>Fast-fails a wrong OLD password / tampered KDF field via the same distinct
    /// <see cref="WrongKeyOrTamperError"/> as <see cref="Open"/>, before any write — a change
    /// attempted with the wrong current password makes no state change at all.</para>
    /// </summary>
    /// <param name="superblock">The current superblock carrying the encryption fields and KeyStore pointer.</param>
    /// <param name="oldPassword">The current password (NFC-normalized internally before the KDF).</param>
    /// <param name="newPassword">The replacement password (NFC-normalized internally before the KDF).</param>
    /// <param name="blockManager">A manager over the same file: reads the current KeyStore, appends the re-sealed one.</param>
    /// <param name="newParameters">
    /// Optional Argon2id costs for the new KEK. Null reuses the file's current stored parameters;
    /// pass a stronger set to upgrade the KDF cost as part of the password change.
    /// </param>
    public static Result<PasswordChangeOutcome> ChangePassword(
        Superblock superblock, string oldPassword, string newPassword,
        BlockManager blockManager, Argon2idParams? newParameters = null)
    {
        ArgumentNullException.ThrowIfNull(superblock);
        ArgumentNullException.ThrowIfNull(blockManager);
        if (string.IsNullOrEmpty(newPassword))
            return Result<PasswordChangeOutcome>.Failure("New password cannot be null or empty.");

        if (superblock.EncryptionEnabled == 0)
            return Result<PasswordChangeOutcome>.Failure(
                "Superblock marks the file as plaintext (EncryptionEnabled = 0); there is no password to change.");
        if (superblock.AlgorithmId != AesGcmAlgorithmId)
            return Result<PasswordChangeOutcome>.Failure(
                $"Unsupported encryption AlgorithmId {superblock.AlgorithmId}; this build only supports AES-256-GCM ({AesGcmAlgorithmId}).");

        // Derive the OLD KEK from the STORED KDF params + salt (never compiled-in constants). This
        // also re-verifies the old password below before any write, so a change attempted with the
        // wrong current password fails cleanly with no state change.
        byte[] oldKek;
        try
        {
            oldKek = PasswordKeyDerivation.DeriveKek(
                oldPassword, superblock.KdfType, superblock.KdfParams, superblock.Salt);
        }
        catch (NotSupportedException ex)
        {
            return Result<PasswordChangeOutcome>.Failure($"Password change failed deriving the old KEK: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Result<PasswordChangeOutcome>.Failure($"Password change failed deriving the old KEK: {ex.Message}");
        }

        byte[]? newKek = null;
        try
        {
            // Fast-fail the OLD password before touching the KeyStore or writing anything.
            try
            {
                KeyVerificationToken.Verify(superblock.KeyVerificationToken, oldKek);
            }
            catch (WrongKeyOrTamperError err)
            {
                return Result<PasswordChangeOutcome>.Failure(err);
            }
            catch (ArgumentException ex)
            {
                return Result<PasswordChangeOutcome>.Failure($"Password change failed verifying the token: {ex.Message}");
            }

            // Decrypt the KeyStore with the OLD KEK. The DEK table is carried across UNCHANGED — only
            // the KEK that seals it changes, so every data block of every epoch stays decryptable.
            var keyStoreRes = ReadAndDecryptKeyStore(superblock, oldKek, blockManager);
            if (keyStoreRes.IsFailure)
                return keyStoreRes.VerificationError is not null
                    ? Result<PasswordChangeOutcome>.Failure(keyStoreRes.VerificationError)
                    : Result<PasswordChangeOutcome>.Failure(keyStoreRes.Error);

            var keyStore = keyStoreRes.Value;
            try
            {
                // Fresh CSPRNG salt (and optionally upgraded Argon2id costs) for the new KEK.
                Argon2idParams kdfParams;
                try
                {
                    kdfParams = newParameters ?? Argon2idParams.Unpack(superblock.KdfParams);
                }
                catch (ArgumentException ex)
                {
                    return Result<PasswordChangeOutcome>.Failure($"Password change failed reading the KDF parameters: {ex.Message}");
                }

                var newSalt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
                try
                {
                    newKek = PasswordKeyDerivation.DeriveKek(newPassword, kdfParams, newSalt);
                }
                catch (ArgumentException ex)
                {
                    return Result<PasswordChangeOutcome>.Failure($"Password change failed deriving the new KEK: {ex.Message}");
                }

                var newToken = KeyVerificationToken.Create(newKek);

                // Append the SAME DEK table re-sealed under the new KEK — zero data blocks touched.
                var written = WriteKeyStore(blockManager, newKek, superblock.FileId, keyStore);
                if (written.IsFailure)
                    return Result<PasswordChangeOutcome>.Failure($"Password change failed writing the KeyStore: {written.Error}");

                // The provider copies the new KEK and every DEK; the active epoch is unchanged.
                var provider = new EpochDekProvider(
                    superblock.FileId, keyStore.ActiveEpoch, keyStore.ToProviderEntries(), newKek);
                return Result<PasswordChangeOutcome>.Success(new PasswordChangeOutcome
                {
                    Salt = newSalt,
                    KdfParams = kdfParams.Pack(),
                    KeyVerificationToken = newToken,
                    KeyStore = written.Value,
                    Provider = provider,
                });
            }
            catch (ArgumentException ex)
            {
                return Result<PasswordChangeOutcome>.Failure($"Password change failed building the provider: {ex.Message}");
            }
            finally
            {
                // The provider took its own copies of every DEK; scrub the transient plaintext bytes.
                foreach (var entry in keyStore.Entries)
                    CryptographicOperations.ZeroMemory(entry.Dek);
            }
        }
        finally
        {
            // Both KEKs are scrubbed here: the old one is finished with, and the provider holds its
            // own zeroized copy of the new one (EpochDekProvider copies the KEK it is handed).
            CryptographicOperations.ZeroMemory(oldKek);
            if (newKek is not null)
                CryptographicOperations.ZeroMemory(newKek);
        }
    }

    private static Result<KeyStoreBlock> ReadAndDecryptKeyStore(
        Superblock superblock, ReadOnlySpan<byte> kek, BlockManager blockManager)
    {
        var read = blockManager.Read(superblock.ActiveKeyStoreOffset);
        if (read.IsFailure)
            return read.VerificationError is not null
                ? Result<KeyStoreBlock>.Failure(read.VerificationError)
                : Result<KeyStoreBlock>.Failure(
                    $"reading the KeyStore block at offset {superblock.ActiveKeyStoreOffset}: {read.Error}");

        var block = read.Value;
        if (block.Header.Type != BlockType.KeyStore)
            return Result<KeyStoreBlock>.Failure(
                $"Block at the superblock's KeyStore offset {superblock.ActiveKeyStoreOffset} is a {block.Header.Type}, not a KeyStore block.");
        if (!block.Header.IsEncrypted)
            return Result<KeyStoreBlock>.Failure(
                $"KeyStore block at offset {superblock.ActiveKeyStoreOffset} is not marked encrypted; a KeyStore is always KEK-encrypted (spec Section 9.5).");
        if (!superblock.ActiveKeyStoreBlockId.AsSpan().SequenceEqual(block.Header.BlockId))
            return Result<KeyStoreBlock>.Failure(
                "The block at the superblock's KeyStore offset does not carry the superblock's ActiveKeyStoreBlockId (stale pointer or tampering).");

        byte[] plaintext;
        try
        {
            plaintext = AesGcmBlockCipher.Decrypt(
                block.Payload, kek, superblock.FileId, block.Header.BlockId,
                BlockType.KeyStore, block.Header.KeyEpoch);
        }
        catch (WrongKeyOrTamperError err)
        {
            return Result<KeyStoreBlock>.Failure(err);
        }
        catch (ArgumentException ex)
        {
            return Result<KeyStoreBlock>.Failure($"KeyStore block payload is malformed: {ex.Message}");
        }

        try
        {
            var table = KeyStoreSerializer.Deserialize(plaintext);
            if (table.IsFailure)
                return Result<KeyStoreBlock>.Failure($"KeyStore table is corrupt: {table.Error}");
            return table;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Encrypts and appends a KeyStore block: serializes the DEK table, seals it under the KEK
    /// with AES-256-GCM (AAD-bound to FileId/BlockId/KeyStore type), and appends it as an
    /// encrypted block. Used both at file creation and on password change / key rotation.
    /// </summary>
    /// <param name="blockManager">The manager to append through.</param>
    /// <param name="kek">The 32-byte KEK sealing the KeyStore payload.</param>
    /// <param name="fileId">The 16-byte FileId (an AAD component).</param>
    /// <param name="keyStore">The DEK table to persist.</param>
    /// <returns>The appended block's BlockId, offset, and length — the superblock's new pointer.</returns>
    public static Result<BlockLocation> WriteKeyStore(
        BlockManager blockManager, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> fileId, KeyStoreBlock keyStore)
    {
        ArgumentNullException.ThrowIfNull(blockManager);
        ArgumentNullException.ThrowIfNull(keyStore);

        byte[] plaintext;
        try
        {
            plaintext = KeyStoreSerializer.Serialize(keyStore);
        }
        catch (ArgumentException ex)
        {
            return Result<BlockLocation>.Failure($"KeyStore serialization failed: {ex.Message}");
        }

        try
        {
            // The BlockId is bound into the AAD, so mint it before encrypting, then append under it.
            var blockId = blockManager.MintBlockId();
            var ciphertext = AesGcmBlockCipher.Encrypt(
                plaintext, kek, fileId, blockId, BlockType.KeyStore, keyEpoch: 0);
            return blockManager.Append(
                BlockType.KeyStore, PayloadEncoding.Custom, ciphertext,
                compression: CompressionAlgorithm.None, encrypted: true, keyEpoch: 0, blockId: blockId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>The superblock fields and live provider a freshly created encrypted file needs.</summary>
    public sealed class CreatedEncryption
    {
        /// <summary>CSPRNG salt for the KDF (superblock <c>Salt</c>).</summary>
        public required byte[] Salt { get; init; }

        /// <summary>Packed Argon2id parameters (superblock <c>KdfParams</c>).</summary>
        public required byte[] KdfParams { get; init; }

        /// <summary>The KEK-sealed <c>KeyVerificationToken</c> for the superblock.</summary>
        public required byte[] KeyVerificationToken { get; init; }

        /// <summary>Location of the written KeyStore block (superblock pointer).</summary>
        public required BlockLocation KeyStore { get; init; }

        /// <summary>The live provider (active epoch 0), ready to encrypt/decrypt this session.</summary>
        public required EpochDekProvider Provider { get; init; }

        /// <summary>
        /// Stamps every encryption field onto <paramref name="superblock"/>: EncryptionEnabled,
        /// AlgorithmId, KdfType, KdfParams, Salt, KeyVerificationToken, and the KeyStore pointer.
        /// After this the superblock can be written and the file reopens from file + password alone.
        /// </summary>
        public void ApplyTo(Superblock superblock)
        {
            ArgumentNullException.ThrowIfNull(superblock);
            superblock.EncryptionEnabled = 1;
            superblock.AlgorithmId = AesGcmAlgorithmId;
            superblock.KdfType = (byte)KdfType.Argon2id;
            superblock.KdfParams = (byte[])KdfParams.Clone();
            superblock.Salt = (byte[])Salt.Clone();
            superblock.KeyVerificationToken = (byte[])KeyVerificationToken.Clone();
            superblock.ActiveKeyStoreBlockId = (byte[])KeyStore.BlockId.Clone();
            superblock.ActiveKeyStoreOffset = KeyStore.Offset;
        }
    }

    /// <summary>
    /// Create-time counterpart of <see cref="Open"/>: generates a fresh salt and a first DEK
    /// (epoch 0), derives the KEK from <paramref name="password"/> and the given parameters,
    /// mints the <see cref="KeyVerificationToken"/>, and writes the KEK-encrypted KeyStore block.
    /// Returns the superblock fields to persist and a live provider for the session.
    /// </summary>
    /// <param name="blockManager">The manager to append the KeyStore block through.</param>
    /// <param name="fileId">The 16-byte FileId of the file being created.</param>
    /// <param name="password">The user password.</param>
    /// <param name="parameters">
    /// Argon2id costs to store and use. Null uses <see cref="Argon2idParams.Default"/>; pass a
    /// non-default set to prove parameters are honored from the file, not from constants.
    /// </param>
    public static Result<CreatedEncryption> CreateEncryption(
        BlockManager blockManager, ReadOnlySpan<byte> fileId, string password, Argon2idParams? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(blockManager);
        if (fileId.Length != AesGcmBlockCipher.IdSize)
            return Result<CreatedEncryption>.Failure(
                $"FileId must be exactly {AesGcmBlockCipher.IdSize} bytes, got {fileId.Length}.");

        var kdfParams = parameters ?? Argon2idParams.Default;
        var salt = RandomNumberGenerator.GetBytes(PasswordKeyDerivation.SaltSize);
        var dek0 = RandomNumberGenerator.GetBytes(AesGcmBlockCipher.KeySize);
        var fileIdCopy = fileId.ToArray();

        byte[]? kek = null;
        try
        {
            kek = PasswordKeyDerivation.DeriveKek(password, kdfParams, salt);
            var token = KeyVerificationToken.Create(kek);

            var keyStore = new KeyStoreBlock
            {
                KeyStoreVersion = KeyStoreBlock.CurrentVersion,
                ActiveEpoch = 0,
                Entries =
                {
                    new KeyStoreEntry
                    {
                        Epoch = 0,
                        Dek = dek0,
                        CreatedTimestamp = DateTime.UtcNow.Ticks,
                        Retired = false,
                    },
                },
            };

            var written = WriteKeyStore(blockManager, kek, fileIdCopy, keyStore);
            if (written.IsFailure)
                return Result<CreatedEncryption>.Failure($"Encrypted file creation failed writing the KeyStore: {written.Error}");

            // Provider takes its own copy of the KEK and DEK, zeroized on its dispose.
            var provider = new EpochDekProvider(
                fileIdCopy, 0, keyStore.ToProviderEntries(), kek);

            return Result<CreatedEncryption>.Success(new CreatedEncryption
            {
                Salt = salt,
                KdfParams = kdfParams.Pack(),
                KeyVerificationToken = token,
                KeyStore = written.Value,
                Provider = provider,
            });
        }
        finally
        {
            if (kek is not null)
                CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(dek0);
        }
    }
}

/// <summary>
/// The <see cref="IEncryptionBootstrap"/> implementation that fills the <see cref="CleanOpener"/>
/// seam (spec Section 10.2 step 2) with the real chain: constructed with the file stream and the
/// password, its <see cref="Bootstrap"/> runs <see cref="EncryptionBootstrap.Open"/> over the
/// selected superblock and exposes the resulting <see cref="Provider"/>. This is the production
/// wiring that turns an encrypted-file open into a working <see cref="EpochDekProvider"/> instead
/// of the <see cref="OpenOutcomeKind.EncryptionBootstrapRequired"/> seam.
///
/// <para>The bootstrap reads the KeyStore block through its own short-lived
/// <see cref="BlockManager"/> over the same stream (the opener has not built its manager yet at
/// the seam). The caller owns the stream; disposing this bootstrap does not close it. The
/// exposed <see cref="Provider"/> is the caller's to dispose.</para>
/// </summary>
public sealed class PasswordEncryptionBootstrap : IEncryptionBootstrap
{
    private readonly FileStream _stream;
    private readonly string _password;

    /// <summary>The provider built by <see cref="Bootstrap"/>; null until a successful bootstrap.</summary>
    public EpochDekProvider? Provider { get; private set; }

    /// <summary>Binds the bootstrap to a file stream and a password.</summary>
    public PasswordEncryptionBootstrap(FileStream stream, string password)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _password = password ?? throw new ArgumentNullException(nameof(password));
    }

    /// <inheritdoc/>
    public Result Bootstrap(Superblock superblock)
    {
        ArgumentNullException.ThrowIfNull(superblock);

        // Idempotent: recovery can bootstrap once for the dirty scan and again for the
        // post-heal clean re-open (spec Section 10.2 step 2). The derived provider is the
        // same for the same file+password, so a second call is a no-op — re-deriving would
        // pay the KDF cost twice and leak the first provider's key material.
        if (Provider is not null)
            return Result.Success();

        // The opener has not built its BlockManager yet; use a short-lived one over the same
        // stream to read the KeyStore block. It does not own the stream.
        using var manager = new BlockManager(
            _stream, maxPayloadLength: superblock.MaxPayloadLength, ownsStream: false);

        var opened = EncryptionBootstrap.Open(superblock, _password, manager);
        if (opened.IsFailure)
            return opened.VerificationError is not null
                ? Result.Failure(opened.VerificationError)
                : Result.Failure(opened.Error);

        Provider = opened.Value;
        return Result.Success();
    }
}
