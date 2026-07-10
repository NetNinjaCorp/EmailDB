namespace EmailDB.Format.V3;

/// <summary>
/// The outcome of a read-path fetch — <see cref="EmailManager.GetEmail"/> (Tier 3 raw MIME) or
/// <see cref="EmailManager.GetMetadata"/> (Tier 2 metadata payload). A lookup for an
/// <b>unknown</b> identity is a SUCCESSFUL result with <see cref="Found"/> == <c>false</c> and a
/// null <see cref="Content"/> — a clean not-found, never an exception or a failed
/// <see cref="Result"/> (EmailDB_FileFormat_Spec.md Section 13; the read path reserves failures
/// for I/O errors, corruption, and tamper). When <see cref="Found"/> is true, <see cref="Content"/>
/// is the decrypted, decompressed, verified payload (which may legitimately be empty).
/// </summary>
public readonly record struct EmailReadResult
{
    private EmailReadResult(bool found, byte[]? content)
    {
        Found = found;
        Content = content;
    }

    /// <summary>True when the identity resolved to a stored block; false for a clean not-found.</summary>
    public bool Found { get; }

    /// <summary>
    /// The verified plaintext payload when <see cref="Found"/> — the raw MIME for
    /// <see cref="EmailManager.GetEmail"/>, or the Tier 2 metadata bytes for
    /// <see cref="EmailManager.GetMetadata"/>. Null when not found (may be an empty array when found).
    /// </summary>
    public byte[]? Content { get; }

    /// <summary>A hit carrying the verified plaintext payload.</summary>
    public static EmailReadResult Hit(byte[] content) => new(true, content);

    /// <summary>A clean not-found (the identity is not in the primary index).</summary>
    public static EmailReadResult NotFound { get; } = new(false, null);
}
