namespace EmailDB.Format.V3;

/// <summary>
/// Key rotation was refused because the active epoch has reached the on-disk maximum
/// (<see cref="KeyStoreBlock.MaxEpoch"/> = 65535, the width of the 2-byte block-header
/// <c>KeyEpoch</c> field, docs/Encryption.md Section 5). Rotating once more would need epoch
/// 65536, which does not fit — so rotation MUST fail here rather than wrap back to epoch 0
/// (silently colliding with the oldest still-live DEK and mis-decrypting its blocks).
///
/// <para>This is a distinct, operational failure — not a <see cref="WrongKeyOrTamperError"/>
/// (nothing is wrong with the key) and not corruption. It is raised <b>before</b> any KeyStore
/// block is written, so a refused rotation leaves the file byte-for-byte unchanged. The escape
/// hatch is compaction re-encryption (docs/Encryption.md Section 5, Compaction.md Section 4),
/// which consolidates blocks onto the active epoch and prunes old DEKs — reclaiming epoch space
/// long before 65535 is ever reached in practice.</para>
/// </summary>
public sealed class EpochExhaustedError : InvalidOperationException
{
    /// <summary>The active epoch at the point rotation was refused (always <see cref="KeyStoreBlock.MaxEpoch"/>).</summary>
    public ushort ActiveEpoch { get; }

    /// <summary>Creates the error for an active epoch that has hit the rotation ceiling.</summary>
    public EpochExhaustedError(ushort activeEpoch)
        : base($"Key rotation refused: the active epoch is already at the maximum {KeyStoreBlock.MaxEpoch} " +
               "(the 2-byte KeyEpoch ceiling); rotating would overflow and wrap to epoch 0, colliding with an " +
               "existing DEK. Run compaction re-encryption to consolidate epochs and reclaim epoch space " +
               "(docs/Encryption.md Section 5).")
    {
        ActiveEpoch = activeEpoch;
    }
}
