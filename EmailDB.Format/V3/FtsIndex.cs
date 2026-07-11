namespace EmailDB.Format.V3;

/// <summary>
/// The maintenance side of the Phase 1 address trigram index (docs/Search.md Phase 1, task
/// US-EMDB-91-7): it turns each ingested email's From/To/Cc addresses into trigram postings, masks
/// deleted emails, and flushes the accumulated state to the immutable on-disk segment blocks
/// (types 14-17) that the query path (task 91-8) reads. It is the FTS counterpart of
/// <see cref="DateIndex"/> — wired into the same AddEmail/DeleteEmail pipeline and registered in the
/// Checkpoint's generic secondary-index table, here under <see cref="BTreeIndexKind.Fts"/>
/// (IndexKind 3), so the index reopens from one authoritative pointer.
///
/// <para><b>In-memory state.</b> This index holds its full live posting set in memory — the merge of
/// every segment loaded at open plus this session's ingests, minus this session's deletes. Ingest
/// (<see cref="AddEmail"/>) folds an email's field-tagged trigrams into that map; delete
/// (<see cref="RemoveEmail"/>) records a <b>tombstone</b> — segments are immutable, so a delete cannot
/// rewrite them in place; the email is masked from reads immediately and physically dropped at the
/// next flush. Because the query path verifies every trigram candidate against Tier 1 records, a
/// masked email is never returned even before its segment is rewritten.</para>
///
/// <para><b>Segment growth (<see cref="Flush"/>).</b> A flush (driven by Commit/Close) writes the
/// live posting set as one fresh immutable segment — a <see cref="FtsPostingList"/> block per trigram,
/// one <see cref="FtsTermDictionary"/>, one <see cref="FtsSegmentMeta"/> — then a new
/// <see cref="FtsSearchRoot"/> naming it, retiring the previous root (its blocks become compaction
/// orphans exactly as superseded B+-tree nodes do). Tombstoned emails are excluded from the new
/// segment, so a delete becomes durable by omission. The block structures support a root naming many
/// segments (the query path unions them), so incremental multi-segment growth is a future refinement
/// this layout already permits.</para>
///
/// <para><b>Rebuildable.</b> The FTS index takes part in no crash-recovery guarantee (docs/Search.md):
/// its blocks are written only at flush, so emails added since the last Checkpoint and recovered from
/// the WAL are simply not yet indexed until re-ingested or the index is rebuilt — never a correctness
/// problem, because Tier 1 verification bounds every query. Not thread-safe.</para>
/// </summary>
public sealed class FtsIndex
{
    // Full live postings: trigram → (email → OR of the fields the trigram appeared in for that email).
    private readonly SortedDictionary<Trigram, SortedDictionary<EmailHashedID, AddressField>> _postings = new();
    // Emails that currently have at least one live posting (excludes tombstoned/never-indexed emails).
    private readonly HashSet<EmailHashedID> _indexedEmails = new();
    // Emails deleted since the last flush; masked from reads now, purged from the segment at flush.
    private readonly HashSet<EmailHashedID> _tombstones = new();

    private ulong _nextSegmentSequence;
    private ulong _nextSearchRootSequence;
    private bool _dirty;
    private BlockLocation? _committedSearchRootLocation;

    /// <summary>Creates an empty index (a fresh file, or one the Checkpoint names no FTS root for).</summary>
    public FtsIndex() { }

    private FtsIndex(ulong nextSegmentSequence, ulong nextSearchRootSequence, BlockLocation committedSearchRootLocation)
    {
        _nextSegmentSequence = nextSegmentSequence;
        _nextSearchRootSequence = nextSearchRootSequence;
        _committedSearchRootLocation = committedSearchRootLocation;
    }

    /// <summary>Number of emails with at least one live (non-tombstoned) posting.</summary>
    public int IndexedEmailCount => _indexedEmails.Count;

    /// <summary>Number of distinct trigrams with at least one live posting.</summary>
    public int DistinctTrigramCount
    {
        get
        {
            int n = 0;
            foreach (var kv in _postings)
                if (HasLivePosting(kv.Value))
                    n++;
            return n;
        }
    }

    /// <summary>True when an ingest or delete since the last flush has yet to be written to a segment.</summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// The location of the currently-committed <see cref="FtsSearchRoot"/> block, or null when the
    /// index has never been flushed. The Checkpoint re-registers this (unchanged) location when a
    /// commit finds the index clean, so the IndexKind-3 entry survives every commit.
    /// </summary>
    public BlockLocation? CommittedSearchRootLocation => _committedSearchRootLocation;

    // ------------------------------------------------------------- Ingest / delete

    /// <summary>
    /// Indexes one email's addresses (task 91-7 ingest): extracts the distinct sliding-window trigrams
    /// of <paramref name="from"/> (tagged <see cref="AddressField.From"/>), each of <paramref name="to"/>
    /// (<see cref="AddressField.To"/>), and each of <paramref name="cc"/> (<see cref="AddressField.Cc"/>),
    /// and folds them into the live posting set, OR-merging the field flags of a trigram that appears in
    /// more than one of the email's fields. An address shorter than three characters contributes no
    /// trigram; an email with no trigrams at all is a no-op (nothing to search). Re-adding a previously
    /// tombstoned email clears its tombstone.
    /// </summary>
    /// <param name="email">The email's Tier 1 content identity (the posting key).</param>
    /// <param name="from">The From address text (may be null/empty).</param>
    /// <param name="to">The To address texts (may be null/empty).</param>
    /// <param name="cc">The Cc address texts (may be null/empty).</param>
    public Result AddEmail(EmailHashedID email, string? from, IEnumerable<string>? to, IEnumerable<string>? cc)
    {
        var fields = new Dictionary<Trigram, AddressField>();
        Accumulate(fields, from, AddressField.From);
        if (to is not null)
            foreach (var addr in to)
                Accumulate(fields, addr, AddressField.To);
        if (cc is not null)
            foreach (var addr in cc)
                Accumulate(fields, addr, AddressField.Cc);

        if (fields.Count == 0)
            return Result.Success(); // Nothing indexable (all addresses shorter than a trigram).

        _tombstones.Remove(email);
        foreach (var (trigram, field) in fields)
        {
            if (!_postings.TryGetValue(trigram, out var emails))
            {
                emails = new SortedDictionary<EmailHashedID, AddressField>();
                _postings.Add(trigram, emails);
            }
            emails[email] = emails.TryGetValue(email, out var existing) ? existing | field : field;
        }
        _indexedEmails.Add(email);
        _dirty = true;
        return Result.Success();
    }

    /// <summary>
    /// Masks one email from the index (task 91-7 delete): tombstones the identity so it is excluded
    /// from every read immediately and dropped from the segment at the next <see cref="Flush"/>.
    /// Deleting an email the index does not hold is a no-op.
    /// </summary>
    /// <param name="email">The identity to remove.</param>
    /// <returns>True when the email was indexed and is now masked; false when it was not indexed.</returns>
    public bool RemoveEmail(EmailHashedID email)
    {
        if (!_indexedEmails.Remove(email))
            return false;
        _tombstones.Add(email);
        _dirty = true;
        return true;
    }

    private static void Accumulate(Dictionary<Trigram, AddressField> fields, string? text, AddressField field)
    {
        foreach (var trigram in TrigramExtractor.Extract(text))
            fields[trigram] = fields.TryGetValue(trigram, out var existing) ? existing | field : field;
    }

    // ------------------------------------------------------------- Read (query support / tests)

    /// <summary>
    /// The live postings for <paramref name="trigram"/> — the emails whose addresses contain it, each
    /// with its OR-merged field flags — ascending by email and excluding tombstoned emails. Empty when
    /// the trigram is not indexed. This is the per-trigram leaf the query path (task 91-8) intersects.
    /// </summary>
    public IReadOnlyList<FtsPosting> GetPostings(Trigram trigram)
    {
        if (!_postings.TryGetValue(trigram, out var emails))
            return Array.Empty<FtsPosting>();
        var list = new List<FtsPosting>(emails.Count);
        foreach (var (email, fields) in emails)
            if (!_tombstones.Contains(email))
                list.Add(new FtsPosting(email, fields));
        return list;
    }

    /// <summary>True when <paramref name="email"/> currently has at least one live posting.</summary>
    public bool ContainsEmail(EmailHashedID email) => _indexedEmails.Contains(email);

    /// <summary>
    /// The trigram-intersection half of the address query path (task 91-8, docs/Search.md Phase 1):
    /// returns the emails whose addresses contain <b>every</b> trigram in <paramref name="queryTrigrams"/>
    /// within a single requested field, each tagged with the fields in which the whole trigram set
    /// co-occurs. This is a <i>necessary</i> filter, not a sufficient one — a candidate has all the
    /// query's trigrams but may still not contain the query substring (e.g. the trigrams appear in a
    /// different order), so the caller (<see cref="EmailManager.SearchAddresses"/>) verifies each
    /// candidate's actual Tier 1 address text before returning it.
    ///
    /// <para><b>Per-field AND.</b> A real substring match lies entirely inside one address (one field),
    /// so the intersection is computed <i>within each field</i>: a candidate's returned
    /// <see cref="FtsCandidate.Fields"/> is the set of requested fields F for which every query trigram
    /// has a posting in F for that email. An email that has one trigram only in From and another only in
    /// To is therefore <b>not</b> a candidate for either field — no single address holds the whole
    /// substring. Tombstoned emails are excluded (they carry no live posting).</para>
    ///
    /// <para>Results are ordered ascending by <see cref="EmailHashedID"/> for determinism. An empty
    /// trigram set (a query shorter than a trigram), an empty field filter, or any query trigram that is
    /// absent from the index yields no candidates.</para>
    /// </summary>
    /// <param name="queryTrigrams">The query's distinct trigrams (same normalization as ingest, via <see cref="TrigramExtractor"/>).</param>
    /// <param name="fieldFilter">The address fields to search (From/To/Cc, or <see cref="AddressField.All"/>).</param>
    public IReadOnlyList<FtsCandidate> FindCandidates(
        IReadOnlyCollection<Trigram> queryTrigrams, AddressField fieldFilter)
    {
        ArgumentNullException.ThrowIfNull(queryTrigrams);
        var mask = fieldFilter & AddressField.All;
        if (mask == AddressField.None || queryTrigrams.Count == 0)
            return Array.Empty<FtsCandidate>();

        // Fetch each distinct query trigram's live postings. A trigram absent from the index means no
        // email can hold the whole substring, so the intersection is empty.
        var distinct = queryTrigrams as HashSet<Trigram> ?? new HashSet<Trigram>(queryTrigrams);
        var lists = new List<IReadOnlyList<FtsPosting>>(distinct.Count);
        foreach (var trigram in distinct)
        {
            var postings = GetPostings(trigram);
            if (postings.Count == 0)
                return Array.Empty<FtsCandidate>();
            lists.Add(postings);
        }

        // Intersect smallest posting list first (fewest survivors to carry forward). `acc` maps each
        // surviving email to the AND of its masked field flags across the trigrams processed so far —
        // i.e. the fields in which EVERY processed trigram co-occurs for that email.
        lists.Sort(static (a, b) => a.Count.CompareTo(b.Count));

        var acc = new Dictionary<EmailHashedID, AddressField>(lists[0].Count);
        foreach (var posting in lists[0])
        {
            var m = posting.Fields & mask;
            if (m != AddressField.None)
                acc[posting.Email] = m;
        }

        for (int i = 1; i < lists.Count && acc.Count > 0; i++)
        {
            var current = new Dictionary<EmailHashedID, AddressField>(lists[i].Count);
            foreach (var posting in lists[i])
            {
                var m = posting.Fields & mask;
                if (m != AddressField.None)
                    current[posting.Email] = m;
            }
            var next = new Dictionary<EmailHashedID, AddressField>(acc.Count);
            foreach (var (email, fields) in acc)
            {
                if (current.TryGetValue(email, out var other))
                {
                    var and = fields & other;
                    if (and != AddressField.None)
                        next[email] = and;
                }
            }
            acc = next;
        }

        if (acc.Count == 0)
            return Array.Empty<FtsCandidate>();

        var result = new List<FtsCandidate>(acc.Count);
        foreach (var (email, fields) in acc)
            result.Add(new FtsCandidate(email, fields));
        result.Sort(static (a, b) => a.Email.CompareTo(b.Email));
        return result;
    }

    // ------------------------------------------------------------- Flush

    /// <summary>
    /// Writes the live posting set as one fresh immutable segment plus a new
    /// <see cref="FtsSearchRoot"/>, and returns the root block's location for the Checkpoint's
    /// IndexKind-3 registration. When the index is unchanged since the last flush the existing root
    /// location is returned unwritten (so a commit still re-registers it); when the index is empty a
    /// zero-segment root is written the first time and null thereafter. On success tombstoned emails
    /// are physically dropped and the dirty flag clears.
    /// </summary>
    /// <param name="store">The FTS block store (encrypts under a loaded provider).</param>
    /// <returns>The location to register under IndexKind 3, or null when there is nothing to register.</returns>
    public Result<BlockLocation?> Flush(FtsBlockStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!_dirty)
            return Result<BlockLocation?>.Success(_committedSearchRootLocation);

        // Build one posting-list block per trigram that still has a live posting.
        var segmentIds = new List<byte[]>();
        var termMap = new Dictionary<Trigram, byte[]>();
        foreach (var (trigram, emails) in _postings)
        {
            var postings = new List<FtsPosting>(emails.Count);
            foreach (var (email, fields) in emails)
                if (!_tombstones.Contains(email))
                    postings.Add(new FtsPosting(email, fields));
            if (postings.Count == 0)
                continue; // Every posting for this trigram was tombstoned.

            var list = FtsPostingList.FromPostings(trigram, postings);
            var written = store.AppendPostingList(list);
            if (written.IsFailure)
                return Result<BlockLocation?>.Failure($"FTS flush failed writing a posting-list block: {written.Error}");
            termMap[trigram] = written.Value.BlockId;
        }

        // A non-empty segment: term dictionary → segment meta → SearchRoot naming the one segment.
        if (termMap.Count > 0)
        {
            var dictionary = FtsTermDictionary.FromMap(termMap);
            var dictLoc = store.AppendTermDictionary(dictionary);
            if (dictLoc.IsFailure)
                return Result<BlockLocation?>.Failure($"FTS flush failed writing the term dictionary: {dictLoc.Error}");

            var meta = FtsSegmentMeta.Create(
                _nextSegmentSequence, dictLoc.Value.BlockId, (uint)termMap.Count, (uint)_indexedEmails.Count);
            var metaLoc = store.AppendSegmentMeta(meta);
            if (metaLoc.IsFailure)
                return Result<BlockLocation?>.Failure($"FTS flush failed writing the segment meta: {metaLoc.Error}");
            segmentIds.Add(metaLoc.Value.BlockId);
        }

        var root = FtsSearchRoot.Create(_nextSearchRootSequence, segmentIds);
        var rootLoc = store.AppendSearchRoot(root);
        if (rootLoc.IsFailure)
            return Result<BlockLocation?>.Failure($"FTS flush failed writing the search root: {rootLoc.Error}");

        // Commit the in-memory state: advance sequences, drop tombstoned postings, mark clean.
        if (termMap.Count > 0)
            _nextSegmentSequence++;
        _nextSearchRootSequence++;
        PurgeTombstones();
        _committedSearchRootLocation = rootLoc.Value;
        _dirty = false;
        return Result<BlockLocation?>.Success(_committedSearchRootLocation);
    }

    private void PurgeTombstones()
    {
        if (_tombstones.Count == 0)
            return;
        var emptyTrigrams = new List<Trigram>();
        foreach (var (trigram, emails) in _postings)
        {
            foreach (var tomb in _tombstones)
                emails.Remove(tomb);
            if (emails.Count == 0)
                emptyTrigrams.Add(trigram);
        }
        foreach (var trigram in emptyTrigrams)
            _postings.Remove(trigram);
        _tombstones.Clear();
    }

    private bool HasLivePosting(SortedDictionary<EmailHashedID, AddressField> emails)
    {
        if (_tombstones.Count == 0)
            return emails.Count > 0;
        foreach (var email in emails.Keys)
            if (!_tombstones.Contains(email))
                return true;
        return false;
    }

    // ------------------------------------------------------------- Reconstruction

    /// <summary>
    /// Rebuilds the in-memory index from the on-disk segments a Checkpoint named (task 91-7 reopen):
    /// resolves and reads the <see cref="FtsSearchRoot"/> at <paramref name="ftsRoot"/>, then each of
    /// its segments' term dictionary and posting-list blocks, merging every posting back into memory so
    /// a committed ingest/delete survives close/reopen. A null pointer is an empty index.
    /// </summary>
    /// <param name="store">The FTS block store (decrypts under a loaded provider) with a resolver.</param>
    /// <param name="ftsRoot">The resolved IndexKind-3 root, or null for an index the Checkpoint names no root for.</param>
    public static Result<FtsIndex> Reconstruct(FtsBlockStore store, ResolvedRoot? ftsRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (ftsRoot is null)
            return Result<FtsIndex>.Success(new FtsIndex());

        var rootRead = store.ReadSearchRoot(ftsRoot.Offset);
        if (rootRead.IsFailure)
            return Result<FtsIndex>.Failure($"FTS reopen failed reading the search root: {rootRead.Error}");
        var searchRoot = rootRead.Value;

        var rootLocation = new BlockLocation
        {
            BlockId = (byte[])ftsRoot.BlockId.Clone(),
            Offset = ftsRoot.Offset,
            TotalBlockLength = ftsRoot.TotalBlockLength,
        };
        var index = new FtsIndex(
            nextSegmentSequence: 0,
            nextSearchRootSequence: searchRoot.SearchRootSequence + 1,
            committedSearchRootLocation: rootLocation);

        ulong maxSegmentSequence = 0;
        bool anySegment = false;
        foreach (var segmentId in searchRoot.SegmentMetaBlockIds)
        {
            var metaOffset = store.ResolveOffset(segmentId);
            if (metaOffset.IsFailure)
                return Result<FtsIndex>.Failure($"FTS reopen failed resolving a segment: {metaOffset.Error}");
            var metaRead = store.ReadSegmentMeta(metaOffset.Value);
            if (metaRead.IsFailure)
                return Result<FtsIndex>.Failure($"FTS reopen failed reading a segment meta: {metaRead.Error}");
            var meta = metaRead.Value;
            anySegment = true;
            if (meta.SegmentSequence > maxSegmentSequence)
                maxSegmentSequence = meta.SegmentSequence;

            var dictOffset = store.ResolveOffset(meta.TermDictionaryBlockId);
            if (dictOffset.IsFailure)
                return Result<FtsIndex>.Failure($"FTS reopen failed resolving a term dictionary: {dictOffset.Error}");
            var dictRead = store.ReadTermDictionary(dictOffset.Value);
            if (dictRead.IsFailure)
                return Result<FtsIndex>.Failure($"FTS reopen failed reading a term dictionary: {dictRead.Error}");

            foreach (var entry in dictRead.Value.Entries)
            {
                var listOffset = store.ResolveOffset(entry.PostingListBlockId);
                if (listOffset.IsFailure)
                    return Result<FtsIndex>.Failure($"FTS reopen failed resolving a posting list: {listOffset.Error}");
                var listRead = store.ReadPostingList(listOffset.Value);
                if (listRead.IsFailure)
                    return Result<FtsIndex>.Failure($"FTS reopen failed reading a posting list: {listRead.Error}");

                foreach (var posting in listRead.Value.Postings)
                {
                    if (!index._postings.TryGetValue(entry.Trigram, out var emails))
                    {
                        emails = new SortedDictionary<EmailHashedID, AddressField>();
                        index._postings.Add(entry.Trigram, emails);
                    }
                    emails[posting.Email] = emails.TryGetValue(posting.Email, out var existing)
                        ? existing | posting.Fields
                        : posting.Fields;
                    index._indexedEmails.Add(posting.Email);
                }
            }
        }

        index._nextSegmentSequence = anySegment ? maxSegmentSequence + 1 : 0;
        return Result<FtsIndex>.Success(index);
    }
}
