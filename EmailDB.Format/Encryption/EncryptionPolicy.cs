using EmailDB.Format.Models;

namespace EmailDB.Format.Encryption;

/// <summary>
/// Controls which block types are encrypted. Used by encryption providers
/// to decide whether a given block type should be encrypted.
/// </summary>
public sealed class EncryptionPolicy
{
    private readonly HashSet<BlockType> _encryptedTypes;

    public EncryptionPolicy(IEnumerable<BlockType> encryptedTypes)
    {
        _encryptedTypes = new HashSet<BlockType>(encryptedTypes ?? throw new ArgumentNullException(nameof(encryptedTypes)));
    }

    /// <summary>
    /// Default policy: encrypts EmailContent, Folder, FolderTree, Segment, and WAL.
    /// Leaves Metadata, Cleanup, BTree nodes, and IndexRoot unencrypted.
    /// </summary>
    public static EncryptionPolicy Default { get; } = new(new[]
    {
        BlockType.EmailContent,
        BlockType.Folder,
        BlockType.FolderTree,
        BlockType.Segment,
        BlockType.WAL
    });

    /// <summary>
    /// Full policy: encrypts all block types except Metadata.
    /// </summary>
    public static EncryptionPolicy Full { get; } = new(
        Enum.GetValues<BlockType>().Where(bt => bt != BlockType.Metadata)
    );

    /// <summary>
    /// Determines whether the given block type should be encrypted under this policy.
    /// </summary>
    public bool ShouldEncrypt(BlockType blockType) => _encryptedTypes.Contains(blockType);
}
