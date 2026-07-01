using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

// =============================================================================
// Search Strategy Benchmark: Listing Page Scan vs Trigram Index
//
// Usage: dotnet run -c Release [email_count]
//   Default: 1,000,000 emails. Use 10000000 for full 10M test (needs ~16 GB RAM).
//
// Approach 1 (Page Scan):
//   - Records packed into pages of 80, each page AES-256-GCM encrypted
//   - Search = decrypt every page, substring match against subject/sender/preview
//
// Approach 2 (Trigram Index):
//   - In-memory index: trigram -> int[] posting list
//   - Query = decompose into trigrams, intersect posting lists, post-filter
// =============================================================================

int totalEmails = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 1_000_000;
const int PageSize = 80;
int totalPages = totalEmails / PageSize;

Console.WriteLine($"Search Strategy Benchmark — {totalEmails:N0} emails");
Console.WriteLine($"Pages: {totalPages:N0} (×{PageSize} records each)");
Console.WriteLine(new string('=', 70));

// ---------------------------------------------------------------------------
// Step 1: Generate data and build encrypted pages in one pass
// (Don't keep raw strings — pack into pages immediately to save memory)
// ---------------------------------------------------------------------------
Console.Write("\nGenerating + encrypting pages...");
var sw = Stopwatch.StartNew();

var rng = new Random(42);
var wordPool = GetWordPool();
var firstNames = GetFirstNamePool();
var lastNames = GetLastNamePool();
var domainPool = new[] { "gmail.com", "outlook.com", "yahoo.com", "company.co", "example.org",
    "fastmail.com", "protonmail.com", "icloud.com", "hotmail.com", "work.internal" };

var aesKey = new byte[32];
RandomNumberGenerator.Fill(aesKey);

// We need raw strings for trigram indexing, but we'll process in page-sized batches
// and build the trigram index as we go, freeing each batch after page encryption.
var encryptedPages = new byte[totalPages][];

// Trigram index: trigram -> list of record indices (compact)
var trigramIndex = new Dictionary<string, List<int>>(200_000);

int rareCount = 0, midCount = 0, prefixCount = 0, gmailCount = 0;
const string RareNeedle = "ZQXINVOICE";
const string MidNeedle = "quarterly";

var pageSb = new StringBuilder(40_000);

for (int p = 0; p < totalPages; p++)
{
    pageSb.Clear();
    int baseIdx = p * PageSize;

    for (int r = 0; r < PageSize; r++)
    {
        int idx = baseIdx + r;

        var subject = GenerateSubject(rng, wordPool);
        var sender = GenerateSender(rng, firstNames, lastNames, domainPool);
        var preview = GeneratePreview(rng, wordPool);

        // Inject needles
        double roll = rng.NextDouble();
        if (roll < 0.0001) { subject = RareNeedle + " " + subject; rareCount++; }
        if (roll < 0.01) { subject = subject + " " + MidNeedle; midCount++; }
        if (roll < 0.002) { sender = "alice@company.co"; prefixCount++; }
        if (sender.Contains("gmail.com", StringComparison.OrdinalIgnoreCase)) gmailCount++;

        // Add trigrams for this record
        AddTrigrams(trigramIndex, subject, idx);
        AddTrigrams(trigramIndex, sender, idx);
        AddTrigrams(trigramIndex, preview, idx);

        // Pack into page
        pageSb.Append(subject).Append('\0');
        pageSb.Append(sender).Append('\0');
        pageSb.Append(preview).Append('\0');
    }

    // Encrypt page
    var plaintext = Encoding.UTF8.GetBytes(pageSb.ToString());
    var nonce = new byte[12];
    RandomNumberGenerator.Fill(nonce);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[16];
    using var aes = new AesGcm(aesKey, 16);
    aes.Encrypt(nonce, plaintext, ciphertext, tag);

    var encrypted = new byte[12 + ciphertext.Length + 16];
    Buffer.BlockCopy(nonce, 0, encrypted, 0, 12);
    Buffer.BlockCopy(ciphertext, 0, encrypted, 12, ciphertext.Length);
    Buffer.BlockCopy(tag, 0, encrypted, 12 + ciphertext.Length, 16);
    encryptedPages[p] = encrypted;

    if (p % (totalPages / 10 + 1) == 0 && p > 0)
        Console.Write($" {p * 100 / totalPages}%");
}

sw.Stop();
long totalEncryptedBytes = 0;
for (int i = 0; i < encryptedPages.Length; i++) totalEncryptedBytes += encryptedPages[i].Length;
long trigramEntries = 0;
foreach (var kv in trigramIndex) trigramEntries += kv.Value.Count;

Console.WriteLine($" done in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"  Needles: rare={rareCount}, mid={midCount}, prefix={prefixCount}");
Console.WriteLine($"  Natural gmail.com senders: {gmailCount:N0}");
Console.WriteLine($"  Encrypted page data: {totalEncryptedBytes / (1024.0 * 1024.0):F1} MB");
Console.WriteLine($"  Trigram index: {trigramIndex.Count:N0} unique trigrams, {trigramEntries:N0} posting entries");
long estMem = trigramIndex.Count * 64L + trigramEntries * 4L;
Console.WriteLine($"  Est. trigram index memory: {estMem / (1024.0 * 1024.0):F0} MB");

// We need the raw strings for trigram post-filtering. Re-generate them deterministically
// using the same seed. This is the memory-efficient approach: only one set of strings
// in memory at a time instead of strings + pages + trigrams all at once.
Console.Write("\nRebuilding raw strings for post-filter verification...");
sw.Restart();
var rng2 = new Random(42);
var subjects = new string[totalEmails];
var senders = new string[totalEmails];
var previews = new string[totalEmails];

for (int i = 0; i < totalEmails; i++)
{
    subjects[i] = GenerateSubject(rng2, wordPool);
    senders[i] = GenerateSender(rng2, firstNames, lastNames, domainPool);
    previews[i] = GeneratePreview(rng2, wordPool);

    double roll = rng2.NextDouble();
    if (roll < 0.0001) subjects[i] = RareNeedle + " " + subjects[i];
    if (roll < 0.01) subjects[i] = subjects[i] + " " + MidNeedle;
    if (roll < 0.002) senders[i] = "alice@company.co";
}
sw.Stop();
Console.WriteLine($" done in {sw.Elapsed.TotalSeconds:F1}s");

// Force a GC before benchmarking to get stable numbers
GC.Collect(2, GCCollectionMode.Aggressive, true, true);

// ---------------------------------------------------------------------------
// Benchmark queries
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"{"Query",-25} {"Type",-10} {"Scan (ms)",-12} {"Trigram (ms)",-14} {"Scan Hits",-12} {"Tri Hits",-12} {"Speedup",8}");
Console.WriteLine(new string('-', 95));

const string CommonNeedle = "gmail.com";
const string ShortNeedle = "re";
const string PrefixNeedle = "alice@";

RunBenchmark(RareNeedle, "rare");
RunBenchmark(CommonNeedle, "common");
RunBenchmark(MidNeedle, "mid");
RunBenchmark(ShortNeedle, "short");
RunBenchmark(PrefixNeedle, "prefix");
RunBenchmark("meeting tomorrow", "phrase");

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(new string('=', 95));
Console.WriteLine("Notes:");
Console.WriteLine($"  - Page scan decrypts all {totalPages:N0} pages ({totalEncryptedBytes / (1024.0 * 1024.0):F0} MB) per query");
Console.WriteLine($"  - Trigram index uses ~{estMem / (1024.0 * 1024.0):F0} MB resident memory");
Console.WriteLine($"  - Short queries (< 3 chars) cannot use trigram index, must fall back to scan");
Console.WriteLine($"  - Page scan is memory-resident here; in production it would be disk I/O bound");

// =============================================================================
// Methods
// =============================================================================

void RunBenchmark(string query, string queryType)
{
    // --- Page Scan ---
    int scanHits = 0;
    var scanSw = Stopwatch.StartNew();

    for (int p = 0; p < totalPages; p++)
    {
        var page = encryptedPages[p];
        var nonce = new ReadOnlySpan<byte>(page, 0, 12);
        var ciphertext = new ReadOnlySpan<byte>(page, 12, page.Length - 28);
        var tag = new ReadOnlySpan<byte>(page, page.Length - 16, 16);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(aesKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        var text = Encoding.UTF8.GetString(plaintext);
        int searchFrom = 0;
        while (true)
        {
            int pos = text.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) break;
            scanHits++;
            int nextNull = text.IndexOf('\0', pos);
            searchFrom = nextNull < 0 ? text.Length : nextNull + 1;
        }
    }
    scanSw.Stop();
    double scanMs = scanSw.Elapsed.TotalMilliseconds;

    // --- Trigram Index ---
    int trigramHits = 0;
    double trigramMs;

    if (query.Length < 3)
    {
        trigramMs = -1;
        trigramHits = -1;
    }
    else
    {
        var triSw = Stopwatch.StartNew();

        var queryLower = query.ToLowerInvariant();
        var queryTrigrams = new List<string>();
        for (int i = 0; i <= queryLower.Length - 3; i++)
            queryTrigrams.Add(queryLower.Substring(i, 3));

        // Intersect posting lists, smallest first
        List<int>? candidates = null;
        foreach (var tri in queryTrigrams.OrderBy(t =>
            trigramIndex.TryGetValue(t, out var l) ? l.Count : 0))
        {
            if (!trigramIndex.TryGetValue(tri, out var postings))
            {
                candidates = new List<int>();
                break;
            }
            if (candidates == null)
            {
                candidates = new List<int>(postings);
            }
            else
            {
                var set = new HashSet<int>(postings);
                candidates.RemoveAll(c => !set.Contains(c));
            }
            if (candidates.Count == 0) break;
        }
        candidates ??= new List<int>();

        // Post-filter for actual substring match
        foreach (int idx in candidates)
        {
            if (subjects[idx].Contains(query, StringComparison.OrdinalIgnoreCase) ||
                senders[idx].Contains(query, StringComparison.OrdinalIgnoreCase) ||
                previews[idx].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                trigramHits++;
            }
        }

        triSw.Stop();
        trigramMs = triSw.Elapsed.TotalMilliseconds;
    }

    string trigramDisplay = trigramMs < 0 ? "N/A (< 3ch)" : $"{trigramMs:F1}";
    string triHitsDisplay = trigramHits < 0 ? "N/A" : $"{trigramHits:N0}";
    string speedup = trigramMs > 0 ? $"{scanMs / trigramMs:F1}x" : "—";

    Console.WriteLine($"{query,-25} {queryType,-10} {scanMs,-12:F1} {trigramDisplay,-14} {scanHits,-12:N0} {triHitsDisplay,-12} {speedup,8}");
}

static void AddTrigrams(Dictionary<string, List<int>> index, string text, int recordIndex)
{
    var lower = text.ToLowerInvariant();
    for (int i = 0; i <= lower.Length - 3; i++)
    {
        var tri = lower.Substring(i, 3);
        if (!index.TryGetValue(tri, out var list))
        {
            list = new List<int>();
            index[tri] = list;
        }
        if (list.Count == 0 || list[^1] != recordIndex)
            list.Add(recordIndex);
    }
}

static string GenerateSubject(Random rng, string[] words)
{
    int len = rng.Next(3, 10);
    var sb = new StringBuilder(80);
    for (int i = 0; i < len; i++)
    {
        if (i > 0) sb.Append(' ');
        sb.Append(words[rng.Next(words.Length)]);
    }
    if (sb.Length > 128) sb.Length = 128;
    return sb.ToString();
}

static string GenerateSender(Random rng, string[] firstNames, string[] lastNames, string[] domains)
{
    var first = firstNames[rng.Next(firstNames.Length)];
    var last = lastNames[rng.Next(lastNames.Length)];
    var domain = domains[rng.Next(domains.Length)];
    return (rng.Next(4)) switch
    {
        0 => $"{first}.{last}@{domain}",
        1 => $"{first}{last}@{domain}",
        2 => $"{first[0]}{last}@{domain}",
        _ => $"{first}@{domain}"
    };
}

static string GeneratePreview(Random rng, string[] words)
{
    int len = rng.Next(15, 40);
    var sb = new StringBuilder(250);
    for (int i = 0; i < len; i++)
    {
        if (i > 0) sb.Append(' ');
        sb.Append(words[rng.Next(words.Length)]);
    }
    if (sb.Length > 200) sb.Length = 200;
    return sb.ToString();
}

static string[] GetWordPool() => new[] {
    "meeting", "update", "report", "please", "review", "attached", "deadline", "project",
    "budget", "schedule", "follow", "action", "required", "urgent", "hello", "thanks",
    "regards", "confirm", "approve", "discuss", "proposal", "contract", "delivery",
    "payment", "receipt", "order", "shipping", "notification", "reminder", "invitation",
    "response", "feedback", "summary", "important", "request", "information", "available",
    "document", "version", "change", "status", "progress", "complete", "pending", "issue",
    "resolved", "assigned", "priority", "critical", "minor", "enhancement", "feature",
    "release", "deploy", "testing", "staging", "production", "server", "database",
    "network", "security", "access", "permission", "account", "password", "login",
    "session", "timeout", "error", "warning", "success", "failed", "retry", "queue",
    "process", "workflow", "approval", "rejected", "accepted", "submitted", "draft",
    "final", "revised", "original", "copy", "backup", "restore", "archive", "delete",
    "create", "modify", "transfer", "forward", "reply", "bounce", "spam", "filter",
    "folder", "label", "category", "tag", "search", "result", "match", "found",
    "missing", "duplicate", "unique", "shared", "private", "public", "internal",
    "external", "customer", "vendor", "partner", "client", "team", "group", "department",
    "manager", "director", "analyst", "engineer", "developer", "designer", "architect",
    "consultant", "specialist", "coordinator", "administrator", "support", "service",
    "ticket", "incident", "resolution", "escalation", "outage", "maintenance", "upgrade",
    "migration", "integration", "interface", "module", "component", "library", "framework",
    "platform", "application", "system", "infrastructure", "capacity", "performance",
    "optimization", "monitoring", "alerting", "logging", "metrics", "dashboard", "chart",
    "table", "graph", "diagram", "layout", "template", "format", "standard", "policy",
    "procedure", "guideline", "compliance", "audit", "inspection", "certification",
    "training", "onboarding", "handbook", "manual", "tutorial", "webinar", "conference",
    "workshop", "seminar", "presentation", "slides", "agenda", "minutes", "notes",
    "decision", "outcome", "objective", "strategy", "initiative", "milestone", "target",
    "forecast", "estimate", "actual", "variance", "trend", "analysis", "insight",
    "recommendation", "opportunity", "challenge", "risk", "mitigation", "contingency",
    "resource", "allocation", "utilization", "availability", "constraint", "dependency",
    "blocker", "workaround", "solution", "alternative", "comparison", "evaluation",
    "assessment", "benchmark", "baseline", "threshold", "tolerance", "acceptable",
    "exceptional", "nominal", "optimal", "suboptimal", "degraded", "impacted", "affected",
    "downstream", "upstream", "lateral", "vertical", "horizontal", "parallel", "sequential",
    "batch", "stream", "pipeline", "channel", "endpoint", "gateway", "proxy", "cache",
    "buffer", "queue", "stack", "heap", "memory", "storage", "compute", "bandwidth"
};

static string[] GetFirstNamePool() => new[] {
    "james", "mary", "john", "patricia", "robert", "jennifer", "michael", "linda",
    "david", "elizabeth", "william", "barbara", "richard", "susan", "joseph", "jessica",
    "thomas", "sarah", "charles", "karen", "christopher", "lisa", "daniel", "nancy",
    "matthew", "betty", "anthony", "margaret", "mark", "sandra", "donald", "ashley",
    "steven", "kimberly", "paul", "emily", "andrew", "donna", "joshua", "michelle",
    "kenneth", "carol", "kevin", "amanda", "brian", "dorothy", "george", "melissa",
    "timothy", "deborah", "ronald", "stephanie", "edward", "rebecca", "jason", "sharon",
    "jeffrey", "laura", "ryan", "cynthia", "jacob", "kathleen", "gary", "amy",
    "nicholas", "angela", "eric", "shirley", "jonathan", "anna", "stephen", "brenda",
    "larry", "pamela", "justin", "emma", "scott", "nicole", "brandon", "helen",
    "benjamin", "samantha", "samuel", "katherine", "raymond", "christine", "gregory", "debra",
    "frank", "rachel", "alexander", "carolyn", "patrick", "janet", "jack", "catherine",
    "dennis", "maria", "jerry", "heather", "tyler", "diane", "aaron", "ruth",
    "jose", "julie", "adam", "olivia", "nathan", "joyce", "henry", "virginia",
    "peter", "victoria", "zachary", "kelly", "douglas", "lauren", "harold", "christina",
    "carl", "joan", "arthur", "evelyn", "gerald", "judith", "roger", "megan",
    "keith", "andrea", "jeremy", "cheryl", "terry", "hannah", "lawrence", "jacqueline",
    "sean", "martha", "christian", "gloria", "austin", "teresa", "jesse", "ann",
    "ethan", "sara", "walter", "madison", "albert", "frances", "joe", "kathryn",
    "dylan", "janice", "willie", "jean", "bruce", "abigail", "ralph", "alice",
    "gabriel", "judy", "roy", "sophia", "alan", "grace", "wayne", "denise",
    "eugene", "amber", "russell", "doris", "philip", "marilyn", "bobby", "danielle",
    "johnny", "beverly", "howard", "isabella", "fred", "theresa", "louis", "diana"
};

static string[] GetLastNamePool() => new[] {
    "smith", "johnson", "williams", "brown", "jones", "garcia", "miller", "davis",
    "rodriguez", "martinez", "hernandez", "lopez", "gonzalez", "wilson", "anderson",
    "thomas", "taylor", "moore", "jackson", "martin", "lee", "perez", "thompson",
    "white", "harris", "sanchez", "clark", "ramirez", "lewis", "robinson", "walker",
    "young", "allen", "king", "wright", "scott", "torres", "nguyen", "hill",
    "flores", "green", "adams", "nelson", "baker", "hall", "rivera", "campbell",
    "mitchell", "carter", "roberts", "gomez", "phillips", "evans", "turner", "diaz",
    "parker", "cruz", "edwards", "collins", "reyes", "stewart", "morris", "morales",
    "murphy", "cook", "rogers", "gutierrez", "ortiz", "morgan", "cooper", "peterson",
    "bailey", "reed", "kelly", "howard", "ramos", "kim", "cox", "ward",
    "richardson", "watson", "brooks", "chavez", "wood", "james", "bennett", "gray",
    "mendoza", "ruiz", "hughes", "price", "alvarez", "castillo", "sanders", "patel",
    "myers", "long", "ross", "foster", "jimenez", "powell", "jenkins", "perry",
    "russell", "sullivan", "bell", "coleman", "butler", "henderson", "barnes", "gonzales",
    "fisher", "vasquez", "simmons", "graham", "murray", "ford", "castro", "chen",
    "walsh", "cohen", "singh", "sharma", "kumar", "ali", "wong", "tanaka"
};
