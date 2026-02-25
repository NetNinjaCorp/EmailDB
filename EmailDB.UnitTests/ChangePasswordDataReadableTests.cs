using System.Security.Cryptography;
using System.Text;
using EmailDB.Format.Encryption;
using EmailDB.Format.Helpers;
using EmailDB.Format.Models;
using EmailDB.Format.Models.BlockTypes;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion for US-EMDB-56:
/// "All data remains readable with new password after change"
///
/// After a password change the DEKs are preserved (only re-wrapped under a new
/// KEK). Therefore every block that was encrypted before the change must still
/// decrypt correctly when the key store is opened with the new password.
/// </summary>
public class ChangePasswordDataReadableTests
{
    private const int KeySize = 32;
    private const string OldPassword = "old-correct-horse-battery";
    private const string NewPassword = "new-correct-horse-battery";

    private readonly DefaultBlockContentSerializer _serializer = new();

    private static KeyStoreContent CreateSingleEpochContent()
    {
        var dek = new byte[KeySize];
        RandomNumberGenerator.Fill(dek);

        return new KeyStoreContent
        {
            ActiveEpoch = 0,
            Entries = new List<KeyStoreEntry>
            {
                new()
                {
                    Epoch = 0,
                    DEK = dek,
                    Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Retired = false
                }
            }
        };
    }

    private static KeyStoreContent CreateMultiEpochContent()
    {
        var dek0 = new byte[KeySize];
        var dek1 = new byte[KeySize];
        var dek2 = new byte[KeySize];
        RandomNumberGenerator.Fill(dek0);
        RandomNumberGenerator.Fill(dek1);
        RandomNumberGenerator.Fill(dek2);

        return new KeyStoreContent
        {
            ActiveEpoch = 2,
            Entries = new List<KeyStoreEntry>
            {
                new() { Epoch = 0, DEK = dek0, Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 1, DEK = dek1, Timestamp = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), Retired = true },
                new() { Epoch = 2, DEK = dek2, Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Retired = false }
            }
        };
    }

    /// <summary>
    /// Encrypts sample data blocks with the given key store, then performs a
    /// password change (KEK re-wrap) and returns the decrypted key store content
    /// obtained from the new password, plus the ciphertext blocks.
    /// </summary>
    private (KeyStoreContent afterChange, List<(byte[] ciphertext, BlockType type, long blockId, int epoch)> blocks)
        EncryptBlocksThenChangePassword(KeyStoreContent content, (byte[] plaintext, BlockType type, long blockId)[] data)
    {
        // --- Establish old password state ---
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        // Encrypt data blocks with the DEKs
        using var provider = new KeyWrappingEncryptionProvider(content, EncryptionPolicy.Full);
        var encryptedBlocks = new List<(byte[] ciphertext, BlockType type, long blockId, int epoch)>();
        foreach (var (plaintext, type, blockId) in data)
        {
            var ct = provider.Encrypt(plaintext, type, blockId);
            encryptedBlocks.Add((ct, type, blockId, provider.ActiveEpoch));
        }

        // Encrypt key store under old KEK
        var ksManager = new KeyStoreManager(_serializer);
        var encryptedKeyStore = ksManager.EncryptKeyStore(content, oldKek);

        // --- Password change ---
        var decryptedKs = ksManager.DecryptKeyStore(encryptedKeyStore, oldKek);
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncryptedKeyStore = ksManager.EncryptKeyStore(decryptedKs, newKek);

        // Open key store with new password (simulating file reopen)
        var afterChange = ksManager.DecryptKeyStore(reEncryptedKeyStore, newKek);

        return (afterChange, encryptedBlocks);
    }

    // -----------------------------------------------------------------------
    //  Single-epoch tests
    // -----------------------------------------------------------------------

    [Fact]
    public void SingleEpoch_DataReadableAfterPasswordChange()
    {
        var content = CreateSingleEpochContent();
        var plaintext = Encoding.UTF8.GetBytes("Hello, encrypted world!");

        var (afterChange, blocks) = EncryptBlocksThenChangePassword(
            content,
            new[] { (plaintext, BlockType.EmailContent, 1L) });

        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);
        var (ct, type, blockId, epoch) = blocks[0];
        var decrypted = newProvider.Decrypt(ct, type, blockId, epoch);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void SingleEpoch_MultipleBlocksReadableAfterPasswordChange()
    {
        var content = CreateSingleEpochContent();

        var data = new[]
        {
            (Encoding.UTF8.GetBytes("Email body 1"), BlockType.EmailContent, 100L),
            (Encoding.UTF8.GetBytes("Folder data"), BlockType.Folder, 200L),
            (Encoding.UTF8.GetBytes("Segment payload"), BlockType.Segment, 300L),
        };

        var (afterChange, blocks) = EncryptBlocksThenChangePassword(content, data);

        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);

        for (int i = 0; i < data.Length; i++)
        {
            var (ct, type, blockId, epoch) = blocks[i];
            var decrypted = newProvider.Decrypt(ct, type, blockId, epoch);
            Assert.Equal(data[i].Item1, decrypted);
        }
    }

    [Fact]
    public void SingleEpoch_EmptyPayloadReadableAfterPasswordChange()
    {
        var content = CreateSingleEpochContent();
        var plaintext = Array.Empty<byte>();

        var (afterChange, blocks) = EncryptBlocksThenChangePassword(
            content,
            new[] { (plaintext, BlockType.EmailContent, 1L) });

        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);
        var (ct, type, blockId, epoch) = blocks[0];
        var decrypted = newProvider.Decrypt(ct, type, blockId, epoch);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void SingleEpoch_LargePayloadReadableAfterPasswordChange()
    {
        var content = CreateSingleEpochContent();
        var plaintext = new byte[64 * 1024]; // 64 KB
        RandomNumberGenerator.Fill(plaintext);

        var (afterChange, blocks) = EncryptBlocksThenChangePassword(
            content,
            new[] { (plaintext, BlockType.EmailContent, 42L) });

        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);
        var (ct, type, blockId, epoch) = blocks[0];
        var decrypted = newProvider.Decrypt(ct, type, blockId, epoch);

        Assert.Equal(plaintext, decrypted);
    }

    // -----------------------------------------------------------------------
    //  Multi-epoch tests
    // -----------------------------------------------------------------------

    [Fact]
    public void MultiEpoch_DataEncryptedWithActiveEpochReadableAfterPasswordChange()
    {
        var content = CreateMultiEpochContent();
        var plaintext = Encoding.UTF8.GetBytes("Multi-epoch active data");

        var (afterChange, blocks) = EncryptBlocksThenChangePassword(
            content,
            new[] { (plaintext, BlockType.EmailContent, 10L) });

        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);
        var (ct, type, blockId, epoch) = blocks[0];
        var decrypted = newProvider.Decrypt(ct, type, blockId, epoch);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void MultiEpoch_DataEncryptedWithRetiredEpochReadableAfterPasswordChange()
    {
        // Encrypt data with a retired DEK (epoch 0) directly, then change password.
        // Data encrypted with retired DEKs must still be decryptable.
        var content = CreateMultiEpochContent();
        var plaintext = Encoding.UTF8.GetBytes("Old data from retired epoch 0");

        // Encrypt directly with the epoch-0 DEK
        using var epoch0Provider = new AesGcmBlockEncryptionProvider(content.Entries[0].DEK);
        var ct = epoch0Provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 5);

        // Now change password
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);
        var encryptedKs = ksManager.EncryptKeyStore(content, oldKek);

        var decryptedKs = ksManager.DecryptKeyStore(encryptedKs, oldKek);
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncryptedKs = ksManager.EncryptKeyStore(decryptedKs, newKek);

        // Open with new password
        var afterChange = ksManager.DecryptKeyStore(reEncryptedKs, newKek);
        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);

        // Decrypt the block that was encrypted with retired epoch 0
        var decrypted = newProvider.Decrypt(ct, BlockType.EmailContent, blockId: 5, keyEpoch: 0);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void MultiEpoch_DataFromAllEpochsReadableAfterPasswordChange()
    {
        var content = CreateMultiEpochContent();
        var plaintexts = new[]
        {
            Encoding.UTF8.GetBytes("Data from epoch 0"),
            Encoding.UTF8.GetBytes("Data from epoch 1"),
            Encoding.UTF8.GetBytes("Data from epoch 2"),
        };

        // Encrypt one block per epoch directly with each DEK
        var encryptedBlocks = new List<(byte[] ct, int epoch, long blockId)>();
        for (int i = 0; i < content.Entries.Count; i++)
        {
            using var p = new AesGcmBlockEncryptionProvider(content.Entries[i].DEK);
            var ct = p.Encrypt(plaintexts[i], BlockType.EmailContent, blockId: i + 1);
            encryptedBlocks.Add((ct, content.Entries[i].Epoch, i + 1));
        }

        // Password change
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);
        var ksManager = new KeyStoreManager(_serializer);
        var encryptedKs = ksManager.EncryptKeyStore(content, oldKek);

        var decryptedKs = ksManager.DecryptKeyStore(encryptedKs, oldKek);
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        var reEncryptedKs = ksManager.EncryptKeyStore(decryptedKs, newKek);

        var afterChange = ksManager.DecryptKeyStore(reEncryptedKs, newKek);
        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);

        // Verify all blocks from all epochs are still readable
        for (int i = 0; i < encryptedBlocks.Count; i++)
        {
            var (ct, epoch, blockId) = encryptedBlocks[i];
            var decrypted = newProvider.Decrypt(ct, BlockType.EmailContent, blockId, epoch);
            Assert.Equal(plaintexts[i], decrypted);
        }
    }

    // -----------------------------------------------------------------------
    //  All block types remain readable
    // -----------------------------------------------------------------------

    [Fact]
    public void AllEncryptableBlockTypes_ReadableAfterPasswordChange()
    {
        var content = CreateSingleEpochContent();

        var blockTypes = new[]
        {
            BlockType.EmailContent,
            BlockType.Folder,
            BlockType.FolderTree,
            BlockType.Segment,
            BlockType.WAL
        };

        var data = blockTypes.Select((bt, idx) =>
            (Encoding.UTF8.GetBytes($"Payload for {bt}"), bt, (long)(idx + 1))
        ).ToArray();

        var (afterChange, blocks) = EncryptBlocksThenChangePassword(content, data);

        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);

        for (int i = 0; i < data.Length; i++)
        {
            var (ct, type, blockId, epoch) = blocks[i];
            var decrypted = newProvider.Decrypt(ct, type, blockId, epoch);
            Assert.Equal(data[i].Item1, decrypted);
        }
    }

    // -----------------------------------------------------------------------
    //  Repeated password changes
    // -----------------------------------------------------------------------

    [Fact]
    public void DataReadableAfterTwoConsecutivePasswordChanges()
    {
        var content = CreateSingleEpochContent();
        var plaintext = Encoding.UTF8.GetBytes("Survives two password changes");

        // Encrypt data with DEKs
        using var provider = new KeyWrappingEncryptionProvider(content, EncryptionPolicy.Full);
        var ct = provider.Encrypt(plaintext, BlockType.EmailContent, blockId: 1);
        var epoch = provider.ActiveEpoch;

        var ksManager = new KeyStoreManager(_serializer);

        // First password: OldPassword -> NewPassword
        var salt1 = KeyDerivation.GenerateSalt();
        var kek1 = KeyDerivation.DeriveFromPassword(OldPassword, salt1);
        var encKs = ksManager.EncryptKeyStore(content, kek1);

        var decKs = ksManager.DecryptKeyStore(encKs, kek1);
        var salt2 = KeyDerivation.GenerateSalt();
        var kek2 = KeyDerivation.DeriveFromPassword(NewPassword, salt2);
        encKs = ksManager.EncryptKeyStore(decKs, kek2);

        // Second password: NewPassword -> a third password
        const string thirdPassword = "third-correct-horse-battery";
        decKs = ksManager.DecryptKeyStore(encKs, kek2);
        var salt3 = KeyDerivation.GenerateSalt();
        var kek3 = KeyDerivation.DeriveFromPassword(thirdPassword, salt3);
        encKs = ksManager.EncryptKeyStore(decKs, kek3);

        // Open with the third password and verify data is readable
        var afterChange = ksManager.DecryptKeyStore(encKs, kek3);
        using var newProvider = new KeyWrappingEncryptionProvider(afterChange, EncryptionPolicy.Full);
        var decrypted = newProvider.Decrypt(ct, BlockType.EmailContent, blockId: 1, epoch);

        Assert.Equal(plaintext, decrypted);
    }

    // -----------------------------------------------------------------------
    //  Key verification token readable with new password
    // -----------------------------------------------------------------------

    [Fact]
    public void KeyVerificationToken_ReadableWithNewPasswordAfterChange()
    {
        var content = CreateSingleEpochContent();

        // Setup initial encrypted header
        var oldSalt = KeyDerivation.GenerateSalt();
        var oldKek = KeyDerivation.DeriveFromPassword(OldPassword, oldSalt);

        using var oldKekProvider = new AesGcmBlockEncryptionProvider(oldKek);
        var magic = EncryptionHeader.MagicBytes;
        var oldToken = oldKekProvider.Encrypt(magic, BlockType.Metadata, blockId: 0);

        var ksManager = new KeyStoreManager(_serializer);
        var encryptedKs = ksManager.EncryptKeyStore(content, oldKek);

        // Password change
        var decryptedKs = ksManager.DecryptKeyStore(encryptedKs, oldKek);
        var newSalt = KeyDerivation.GenerateSalt();
        var newKek = KeyDerivation.DeriveFromPassword(NewPassword, newSalt);
        ksManager.EncryptKeyStore(decryptedKs, newKek);

        // Rebuild the header with new token
        using var newKekProvider = new AesGcmBlockEncryptionProvider(newKek);
        var newToken = newKekProvider.Encrypt(magic, BlockType.Metadata, blockId: 0);

        var newHeader = new EncryptionHeader
        {
            Magic = EncryptionHeader.MagicBytes,
            SchemeVersion = EncryptionHeader.CurrentSchemeVersion,
            AlgorithmId = EncryptionHeader.AlgorithmAes256Gcm,
            KdfType = EncryptionHeader.KdfArgon2Id,
            Salt = newSalt,
            KeyVerificationToken = newToken
        };

        // Write and read-back the header, then verify with new password
        using var ms = new MemoryStream();
        EncryptionHeaderManager.WriteHeader(ms, newHeader);
        ms.Position = 0;
        var readResult = EncryptionHeaderManager.ReadHeader(ms);

        Assert.True(readResult.IsSuccess);
        var reDerivedKek = KeyDerivation.DeriveFromPassword(NewPassword, readResult.Value.Salt);
        using var verifyProvider = new AesGcmBlockEncryptionProvider(reDerivedKek);
        var decrypted = verifyProvider.Decrypt(readResult.Value.KeyVerificationToken, BlockType.Metadata, blockId: 0);

        Assert.Equal(EncryptionHeader.MagicBytes, decrypted);
    }
}
