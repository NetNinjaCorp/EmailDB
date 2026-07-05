using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EmailDB.Format.V3;

/// <summary>
/// The v3 per-block encryption primitive (EmailDB_FileFormat_Spec.md Section 9.3,
/// docs/Encryption.md Section 3). AES-256-GCM with a fresh 12-byte CSPRNG nonce per
/// operation and mandatory Additional Authenticated Data (AAD) that binds every
/// ciphertext to its identity.
///
/// <para><b>On-disk payload layout</b> (adds exactly <see cref="Overhead"/> = 28 bytes):</para>
/// <code>Nonce (12) ‖ Ciphertext (N) ‖ Tag (16)</code>
///
/// <para><b>Nonce.</b> 12 fully random bytes from a CSPRNG on every encrypt — never
/// derived from the BlockId, an epoch, or a counter. The spec rejects ULID-derived
/// nonces: compaction re-encrypting the same BlockId under the same DEK would collapse
/// nonce uniqueness, and GCM nonce reuse is catastrophic.</para>
///
/// <para><b>AAD (35 bytes), mandatory on both encrypt and decrypt:</b></para>
/// <code>AAD = FileId (16) ‖ BlockId (16) ‖ BlockType (1) ‖ KeyEpoch (2, little-endian)</code>
/// <para>Binds the ciphertext to its identity: a byte-for-byte valid ciphertext moved to a
/// different BlockId, BlockType, KeyEpoch, or FileId fails GCM authentication even though
/// every checksum still passes. There is deliberately no decrypt path that skips AAD.</para>
///
/// <para><b>Key material.</b> This is a stateless primitive: the DEK is borrowed for the
/// duration of a single call and never copied or retained here, so there is nothing to
/// zeroize at this layer. DEK-table ownership, epoch lookup, and zeroization live in the
/// provider that wraps this primitive (US-EMDB-77-6 / story US-EMDB-76).</para>
/// </summary>
public static class AesGcmBlockCipher
{
    /// <summary>GCM nonce length in bytes (spec Section 9.3).</summary>
    public const int NonceSize = 12;

    /// <summary>GCM authentication tag length in bytes (spec Section 9.3).</summary>
    public const int TagSize = 16;

    /// <summary>AES-256 key (DEK) length in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>FileId / BlockId length in bytes (a 16-byte ULID).</summary>
    public const int IdSize = 16;

    /// <summary>AAD length in bytes: FileId (16) + BlockId (16) + BlockType (1) + KeyEpoch (2).</summary>
    public const int AadSize = IdSize + IdSize + 1 + sizeof(ushort); // 35

    /// <summary>Bytes added to the plaintext on disk: Nonce (12) + Tag (16).</summary>
    public const int Overhead = NonceSize + TagSize; // 28

    /// <summary>
    /// Builds the 35-byte AAD for a block: <c>FileId ‖ BlockId ‖ BlockType ‖ KeyEpoch (LE)</c>.
    /// The same bytes MUST be produced on encrypt and decrypt or authentication fails.
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="AadSize"/> bytes; the first 35 bytes are written.</param>
    public static void BuildAad(
        ReadOnlySpan<byte> fileId,
        ReadOnlySpan<byte> blockId,
        BlockType blockType,
        ushort keyEpoch,
        Span<byte> destination)
    {
        if (fileId.Length != IdSize)
            throw new ArgumentException($"FileId must be exactly {IdSize} bytes.", nameof(fileId));
        if (blockId.Length != IdSize)
            throw new ArgumentException($"BlockId must be exactly {IdSize} bytes.", nameof(blockId));
        if (destination.Length < AadSize)
            throw new ArgumentException($"AAD destination must be at least {AadSize} bytes.", nameof(destination));

        fileId.CopyTo(destination);                                  // [0..16)
        blockId.CopyTo(destination[IdSize..]);                       // [16..32)
        destination[IdSize + IdSize] = (byte)blockType;              // [32]
        BinaryPrimitives.WriteUInt16LittleEndian(                    // [33..35)
            destination[(IdSize + IdSize + 1)..], keyEpoch);
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> under <paramref name="dek"/> with a fresh random
    /// nonce and the block's AAD, returning the on-disk payload
    /// <c>Nonce (12) ‖ Ciphertext ‖ Tag (16)</c>.
    /// </summary>
    /// <param name="plaintext">Payload to encrypt (may be empty).</param>
    /// <param name="dek">32-byte data encryption key for the target epoch.</param>
    /// <param name="fileId">16-byte file identifier (AAD component).</param>
    /// <param name="blockId">16-byte block identifier (AAD component).</param>
    /// <param name="blockType">Block type (AAD component).</param>
    /// <param name="keyEpoch">DEK epoch the ciphertext is stamped with (AAD component).</param>
    /// <returns>A new buffer of length <c>plaintext.Length + <see cref="Overhead"/></c>.</returns>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> dek,
        ReadOnlySpan<byte> fileId,
        ReadOnlySpan<byte> blockId,
        BlockType blockType,
        ushort keyEpoch)
    {
        if (dek.Length != KeySize)
            throw new ArgumentException($"DEK must be exactly {KeySize} bytes for AES-256-GCM.", nameof(dek));

        var result = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        var ciphertext = result.AsSpan(NonceSize, plaintext.Length);
        var tag = result.AsSpan(NonceSize + plaintext.Length, TagSize);

        // Fully random CSPRNG nonce, never derived from any ID/epoch/counter.
        RandomNumberGenerator.Fill(nonce);

        Span<byte> aad = stackalloc byte[AadSize];
        BuildAad(fileId, blockId, blockType, keyEpoch, aad);

        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        return result;
    }

    /// <summary>
    /// Decrypts an on-disk payload (<c>Nonce ‖ Ciphertext ‖ Tag</c>) under
    /// <paramref name="dek"/> and the block's AAD. The AAD is mandatory: authentication
    /// fails if any of FileId / BlockId / BlockType / KeyEpoch differs from what the
    /// ciphertext was sealed with.
    /// </summary>
    /// <param name="onDisk">The stored payload; must be at least <see cref="Overhead"/> bytes.</param>
    /// <param name="dek">32-byte DEK for <paramref name="keyEpoch"/>.</param>
    /// <returns>The decrypted plaintext.</returns>
    /// <exception cref="WrongKeyOrTamperError">
    /// The GCM tag / AAD did not verify: wrong key or tampering, not corruption
    /// (spec Section 13). By contract the PayloadChecksum has already verified the bytes,
    /// so reaching here means an authenticity failure, never a bit-flip.
    /// </exception>
    public static byte[] Decrypt(
        ReadOnlySpan<byte> onDisk,
        ReadOnlySpan<byte> dek,
        ReadOnlySpan<byte> fileId,
        ReadOnlySpan<byte> blockId,
        BlockType blockType,
        ushort keyEpoch)
    {
        if (dek.Length != KeySize)
            throw new ArgumentException($"DEK must be exactly {KeySize} bytes for AES-256-GCM.", nameof(dek));
        if (onDisk.Length < Overhead)
            throw new ArgumentException(
                $"Encrypted payload is {onDisk.Length} bytes, shorter than the {Overhead}-byte nonce+tag overhead.",
                nameof(onDisk));

        var nonce = onDisk[..NonceSize];
        var ciphertext = onDisk[NonceSize..^TagSize];
        var tag = onDisk[^TagSize..];

        Span<byte> aad = stackalloc byte[AadSize];
        BuildAad(fileId, blockId, blockType, keyEpoch, aad);

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // Spec Section 13: checksum has already passed, so this is wrong key or
            // tampering (including AAD transplant), a distinct error class from corruption.
            CryptographicOperations.ZeroMemory(plaintext);
            throw WrongKeyOrTamperError.GcmTagFailure(
                blockId: blockId.ToArray(), keyEpoch: keyEpoch, innerException: ex);
        }

        return plaintext;
    }
}
