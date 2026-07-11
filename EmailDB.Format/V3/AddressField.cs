namespace EmailDB.Format.V3;

/// <summary>
/// Which address header a trigram posting came from (docs/Search.md Phase 1:
/// "Combined index over all address fields; postings distinguish the field").
///
/// <para>A <see cref="FtsPosting"/> carries the bitwise-OR of every field in which
/// the posting's trigram appeared for that email, so a single email that has the
/// same trigram in both its From and To addresses is one posting with
/// <c>From | To</c> set. The query path (task 91-8) can therefore restrict a match
/// to a particular header without a separate index. Stored as a single byte.</para>
/// </summary>
[Flags]
public enum AddressField : byte
{
    /// <summary>No field — the empty flag set (never stored in a valid posting).</summary>
    None = 0,

    /// <summary>The From address.</summary>
    From = 1 << 0,

    /// <summary>A To address.</summary>
    To = 1 << 1,

    /// <summary>A Cc address.</summary>
    Cc = 1 << 2,

    /// <summary>Any address field — From, To, or Cc (the default query scope for address search, task 91-8).</summary>
    All = From | To | Cc,
}
