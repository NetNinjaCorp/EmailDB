using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EmailDB.Format.Protobuf.V3;

/// <summary>
/// Extracts the ~200-char plain-text <see cref="EmailMetadata.Preview"/> from an email body
/// (docs/Folder_Listing.md Section 1). The preview is what makes Tier 1 regenerable from Tier 2,
/// so it must be plain text regardless of whether the source body was text or HTML:
/// <list type="bullet">
///   <item>A <c>text/plain</c> body is used verbatim (whitespace collapsed).</item>
///   <item>A <c>text/html</c> body is stripped to plain text: <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c>
///   contents are dropped, tags are removed, HTML entities are decoded, and runs of whitespace are
///   collapsed to single spaces.</item>
/// </list>
/// The result is trimmed and truncated to <see cref="MaxLength"/> characters on a UTF-16 boundary.
/// </summary>
public static partial class PreviewExtractor
{
    /// <summary>Maximum preview length in characters (~200 per docs/Folder_Listing.md Section 1).</summary>
    public const int MaxLength = 200;

    // <script>...</script> and <style>...</style> blocks: their text content is not body text.
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleBlock();

    // Any HTML tag / comment.
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTag();

    // Runs of any whitespace (incl. newlines) → collapsed to a single space.
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A space left before punctuation by tag removal (e.g. "Bob</b>," → "Bob ,").
    [GeneratedRegex(@" +([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>
    /// Produces the preview, preferring the plain-text body and falling back to the HTML body.
    /// Returns an empty string when both are null/empty.
    /// </summary>
    /// <param name="textBody">The <c>text/plain</c> body, or null.</param>
    /// <param name="htmlBody">The <c>text/html</c> body, or null.</param>
    public static string Extract(string? textBody, string? htmlBody)
    {
        if (!string.IsNullOrWhiteSpace(textBody))
            return Normalize(textBody);
        if (!string.IsNullOrWhiteSpace(htmlBody))
            return Normalize(HtmlToPlainText(htmlBody));
        return string.Empty;
    }

    /// <summary>Strips HTML markup to plain text (script/style removed, tags dropped, entities decoded).</summary>
    public static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var noScripts = ScriptStyleBlock().Replace(html, " ");
        // Block-level breaks become spaces so words on separate lines don't run together.
        var withBreaks = noScripts
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</div>", " ", StringComparison.OrdinalIgnoreCase);
        var noTags = HtmlTag().Replace(withBreaks, " ");
        return WebUtility.HtmlDecode(noTags);
    }

    /// <summary>Collapses whitespace, trims, and truncates to <see cref="MaxLength"/> characters.</summary>
    private static string Normalize(string text)
    {
        var collapsed = Whitespace().Replace(text, " ");
        collapsed = SpaceBeforePunctuation().Replace(collapsed, "$1").Trim();
        if (collapsed.Length <= MaxLength)
            return collapsed;

        // Truncate on a UTF-16 boundary so a surrogate pair is never split.
        var cut = MaxLength;
        if (char.IsHighSurrogate(collapsed[cut - 1]))
            cut--;
        return collapsed[..cut];
    }
}
