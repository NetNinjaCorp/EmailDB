using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// No-op encryption provider that passes payloads through unchanged.
/// Used as the default when no encryption is configured, ensuring
/// all code paths work identically with or without encryption.
/// </summary>
public sealed class NullBlockEncryptionProvider : IBlockEncryptionProvider
{
    public static readonly NullBlockEncryptionProvider Instance = new();

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, BlockType blockType, long blockId)
        => plaintext.ToArray();

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, BlockType blockType, long blockId)
        => ciphertext.ToArray();

    public bool ShouldEncrypt(BlockType blockType) => false;

    public int OverheadBytes => 0;

    public bool IsEnabled => false;

    public int ActiveEpoch => 0;

    public void Dispose() { }
}
