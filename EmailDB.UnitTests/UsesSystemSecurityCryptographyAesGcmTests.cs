using System.Reflection;
using System.Security.Cryptography;
using EmailDB.Format.Encryption;
using EmailDB.Format.Models;

namespace EmailDB.UnitTests;

/// <summary>
/// Verifies acceptance criterion: "Uses System.Security.Cryptography.AesGcm"
/// Confirms the provider relies on the standard .NET AES-GCM implementation.
/// </summary>
public class UsesSystemSecurityCryptographyAesGcmTests
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static byte[] GenerateKey()
    {
        var key = new byte[AesGcmBlockEncryptionProvider.KeySize];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    [Fact]
    public void Provider_ReferencesAesGcmType_InMethodBodies()
    {
        // The provider's assembly must reference System.Security.Cryptography.AesGcm
        var providerType = typeof(AesGcmBlockEncryptionProvider);
        var assembly = providerType.Assembly;
        var referencedTypes = assembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .SelectMany(m =>
            {
                if (m is MethodInfo mi && mi.GetMethodBody() != null)
                {
                    return mi.GetMethodBody()!.LocalVariables
                        .Select(lv => lv.LocalType);
                }
                return Enumerable.Empty<Type>();
            })
            .ToList();

        // The Encrypt/Decrypt methods should have AesGcm as a local variable type
        var encryptMethod = providerType.GetMethod("Encrypt")!;
        var decryptMethod = providerType.GetMethod("Decrypt")!;

        var encryptLocals = encryptMethod.GetMethodBody()!.LocalVariables
            .Select(lv => lv.LocalType).ToList();
        var decryptLocals = decryptMethod.GetMethodBody()!.LocalVariables
            .Select(lv => lv.LocalType).ToList();

        Assert.Contains(encryptLocals, t => t == typeof(AesGcm));
        Assert.Contains(decryptLocals, t => t == typeof(AesGcm));
    }

    [Fact]
    public void Encrypt_OutputIsDecryptableByRawAesGcm()
    {
        // Encrypt with the provider, then decrypt with raw System.Security.Cryptography.AesGcm
        // to prove the provider uses the standard AES-GCM implementation.
        var key = GenerateKey();
        using var provider = new AesGcmBlockEncryptionProvider(key);
        var plaintext = new byte[] { 0x54, 0x65, 0x73, 0x74, 0x44, 0x61, 0x74, 0x61 }; // "TestData"
        long blockId = 123;

        var encrypted = provider.Encrypt(plaintext, BlockType.EmailContent, blockId);

        // Extract nonce, ciphertext, and tag per the documented format
        var nonce = encrypted[..NonceSize];
        var ciphertext = encrypted[NonceSize..^TagSize];
        var tag = encrypted[^TagSize..];

        // Decrypt using the raw .NET AesGcm class directly
        using var aesGcm = new AesGcm(key, TagSize);
        var decrypted = new byte[ciphertext.Length];
        aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_AcceptsOutputFromRawAesGcm()
    {
        // Encrypt with raw System.Security.Cryptography.AesGcm, then decrypt with the provider.
        // This proves bidirectional interoperability with the standard AES-GCM.
        var key = GenerateKey();
        var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
        long blockId = 77;

        // Build nonce the same way the provider does: BlockId(8) + Random(4)
        var nonce = new byte[NonceSize];
        BitConverter.TryWriteBytes(nonce.AsSpan(0, 8), blockId);
        RandomNumberGenerator.Fill(nonce.AsSpan(8, 4));

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        // Assemble in the provider's expected format: Nonce(12) + Ciphertext(N) + Tag(16)
        var assembled = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(assembled.AsSpan(0));
        ciphertext.CopyTo(assembled.AsSpan(NonceSize));
        tag.CopyTo(assembled.AsSpan(NonceSize + ciphertext.Length));

        using var provider = new AesGcmBlockEncryptionProvider(key);
        var decrypted = provider.Decrypt(assembled, BlockType.EmailContent, blockId);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesGcmType_IsFromSystemSecurityCryptography()
    {
        // Verify the AesGcm type used is actually from System.Security.Cryptography
        var aesGcmType = typeof(AesGcm);
        Assert.Equal("System.Security.Cryptography", aesGcmType.Namespace);
        Assert.Equal("AesGcm", aesGcmType.Name);
    }
}
