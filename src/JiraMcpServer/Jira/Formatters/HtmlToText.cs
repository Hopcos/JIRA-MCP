using System.Text;
using System.Text.RegularExpressions;

namespace JiraMcpServer.Jira.Formatters;

/// <summary>
/// Minimal HTML-to-text fallback for Confluence content that cannot be rendered as ADF (an old
/// page body, a <c>body.view</c> representation, or a <c>body.storage</c> value when the ADF
/// pipeline is unavailable). Supports block elements (headings, lists, tables, code, quotes),
/// inline emphasis, links, and HTML entities; unknown tags are dropped and their text survives.
/// Not a general parser — it intentionally bookends <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c>
/// bodies so no executable markup reaches the model's context.
/// </summary>
public static class HtmlToText
{
    private static readonly Regex TagPattern = new(
        @"<!--.*?-->|<script\b[^>]*>[\s\S]*?</script>|<style\b[^>]*>[\s\S]*?</style>|<pre\b[^>]*>([\s\S]*?)</pre>|<code\b[^>]*>([\s\S]*?)</code>|</?(p|div|tr|li|ul|ol|blockquote|h[1-6]|br|table|thead|tbody|tfoot)\b[^>]*>|</table>|<img\b[^>]*>|<[^>]+>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BreakPattern = new(
        @"<br\s*/?>|</(p|div|li|ul|ol|blockquote|h[1-6]|tr)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlockStart = new(
        @"<(p|div|li|ul|ol|blockquote|h[1-6]|table|tr)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeadingPattern = new(
        @"<h([1-6])\b[^>]*>([\s\S]*?)</h\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ListItemPattern = new(
        @"<(ul|ol)\b[^>]*>([\s\S]*?)</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Convert an HTML fragment (whole document or partial) to readable plain text.</summary>
    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var block = html;

        // Protect pre/code content from being mangled by the tag stripper: stash it, then restore.
        var stored = new List<string>();
        block = ReplaceWithStore(block, @"<pre\b[^>]*>[\s\S]*?</pre>", stored);
        block = ReplaceWithStore(block, @"<code\b[^>]*>[\s\S]*?</code>", stored);

        // Drop executable/embedded markup up front.
        block = Regex.Replace(block, @"<script\b[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, @"<style\b[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, @"<[^>]*on\w+\s*=\s*[^ >]+[^>]*>", "", RegexOptions.IgnoreCase);

        // Keep some block structure before stripping tags.
        block = Regex.Replace(block, @"</(ul|ol)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, @"</li>", "\n", RegexOptions.IgnoreCase);

        block = Regex.Replace(block, @"</(p|div|blockquote|h[1-6]|td|tr)>", "\n", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, @"<li\b[^>]*>", "  • ", RegexOptions.IgnoreCase);
        block = Regex.Replace(block, @"<t[dh]\b[^>]*>", " | ", RegexOptions.IgnoreCase);

        // Strip remaining tags, then restore stored pre/code.
        block = StripTags(block);
        block = RestoreStored(block, stored);
        block = DecodeEntities(block);

        block = Regex.Replace(block, @"[ \t]+\n", "\n");
        block = Regex.Replace(block, @"\n{3,}", "\n\n");
        return block.Trim();
    }

    /// <summary>Decode the common numeric/named HTML entities so pasted content reads naturally.</summary>
    private static string DecodeEntities(string input)
    {
        input = Regex.Replace(input, @"&#x([0-9a-fA-F]+);", m => CharFromCode(System.Convert.ToInt32(m.Groups[1].Value, 16)));
        input = Regex.Replace(input, @"&#(\d+);", m => CharFromCode(int.Parse(m.Groups[1].Value)));
        return input
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");
    }

    private static string CharFromCode(int code) =>
        code > 0 && code <= 0xFFFF && !char.IsSurrogate((char)code) ? ((char)code).ToString() : "";

    private static string ReplaceWithStore(string input, string pattern, List<string> store)
    {
        return Regex.Replace(input, pattern, match =>
        {
            store.Add(StripTags(match.Value));
            return $"{{{store.Count - 1}}}";
        }, RegexOptions.IgnoreCase);
    }

    private static string RestoreStored(string input, List<string> stored)
    {
        for (var i = 0; i < stored.Count; i++)
        {
            input = input.Replace("{" + i + "}", stored[i]);
        }

        return input;
    }

    private static string StripTags(string input) =>
        Regex.Replace(input, @"<[^>]+>", "", RegexOptions.IgnoreCase);
}
