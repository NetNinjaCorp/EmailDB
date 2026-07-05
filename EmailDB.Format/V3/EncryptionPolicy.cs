namespace EmailDB.Format.V3;

/// <summary>
/// The two at-rest encryption policy sets from spec Section 9.5 (docs/Encryption.md Section 4).
/// A policy decides, per <see cref="BlockType"/>, whether the block write path DEK-encrypts the
/// payload (setting the header <c>Encrypted</c> flag and stamping <c>KeyEpoch</c>) or writes it
/// as plaintext.
///
/// <para>Three rules are invariant across both policies and independent of the enum value:</para>
/// <list type="bullet">
///   <item><b>Metadata, Cleanup, Checkpoint</b> are always plaintext — recovery reads them before
///   any key is available (spec Section 10.2).</item>
///   <item><b>KeyStore</b> is never DEK-encrypted here: it is KEK-encrypted on its own dedicated
///   write path (<see cref="KeyStoreSerializer"/>), so from the DEK write path it is plaintext.</item>
///   <item><b>FTS (14-17) and BloomFilter (18)</b> are always encrypted — trigrams and filter bits
///   reverse to the indexed content, so leaving them plaintext would leak content.</item>
/// </list>
///
/// <para>The policy value only changes the treatment of the structural B+-tree blocks
/// (<see cref="BlockType.BTreeLeaf"/>, <see cref="BlockType.BTreeInternal"/>,
/// <see cref="BlockType.IndexRoot"/>): plaintext under <see cref="Default"/> (keys are opaque
/// hashes, so integrity is checkable without keys), encrypted under <see cref="Full"/>.</para>
/// </summary>
public enum EncryptionPolicy : byte
{
    /// <summary>
    /// Content and content-derived blocks encrypted; the B+-tree structure left plaintext so its
    /// integrity is verifiable without keys (spec Section 9.5, "Default" column).
    /// </summary>
    Default = 0,

    /// <summary>
    /// Everything encrypted except the always-plaintext recovery blocks
    /// (Metadata/Cleanup/Checkpoint) and the KEK-encrypted KeyStore (spec Section 9.5,
    /// "Full" column) — hides index keys at the cost of keyless integrity checks on the B+-tree.
    /// </summary>
    Full = 1,
}

/// <summary>
/// Evaluates the spec Section 9.5 policy tables: given a policy and a block type, whether the
/// block write path must DEK-encrypt the payload.
/// </summary>
public static class EncryptionPolicySet
{
    /// <summary>
    /// Whether a block of <paramref name="type"/> is DEK-encrypted under <paramref name="policy"/>
    /// (spec Section 9.5). Returns <see langword="false"/> for the always-plaintext recovery blocks
    /// and for KeyStore (which is KEK-encrypted on its own path, not here).
    /// </summary>
    public static bool RequiresEncryption(EncryptionPolicy policy, BlockType type) => type switch
    {
        // Always plaintext — read during recovery before any key exists (spec Section 10.2).
        BlockType.Metadata or BlockType.Cleanup or BlockType.Checkpoint => false,

        // KeyStore is KEK-encrypted on its own dedicated path; the DEK write path never touches it.
        BlockType.KeyStore => false,

        // Structural B+-tree blocks: the only types whose treatment depends on the policy.
        BlockType.BTreeLeaf or BlockType.BTreeInternal or BlockType.IndexRoot
            => policy == EncryptionPolicy.Full,

        // Everything else — content, folder structure, WAL, FTS, bloom, and vector/embedding
        // sidecar blocks — is content or content-derived and is encrypted under both policies.
        _ => true,
    };
}
