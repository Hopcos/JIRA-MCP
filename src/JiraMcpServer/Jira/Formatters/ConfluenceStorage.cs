using System.Text;
using System.Text.RegularExpressions;

namespace JiraMcpServer.Jira.Formatters;

/// <summary>
/// Converts Markdown into Confluence "storage format" (XHTML with <c>ac:</c> macros), the
/// representation the classic <c>/wiki/rest/api/content</c> endpoints accept for page bodies.
/// This is a deliberately small converter covering what an LLM typically writes: headings,
/// paragraphs, (nested) bullet/numbered lists, fenced code blocks, blockquotes, tables,
/// horizontal rules and inline bold/italic/strike/code/links. Anything else is escaped as text,
/// so the output is always well-formed and never injects raw markup; callers who need exact
/// control over the markup pass storage format directly instead.
/// </summary>
public static class ConfluenceStorage
{
    private static readonly Regex HeadingRx = new(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceRx = new(@"^\s{0,3}(`{3,}|~{3,})\s*([\w+#.-]*)\s*$", RegexOptions.Compiled);
    private static readonly Regex ListRx = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"^\s{0,3}>\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSepRx = new(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    private static readonly Regex CodeSpanRx = new(@"(`+)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex LinkRx = new(@"\[([^\]\n]+)\]\(\s*([^)\s]+)(?:\s+""[^""]*"")?\s*\)", RegexOptions.Compiled);
    private static readonly Regex BoldRx = new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)", RegexOptions.Compiled);
    private static readonly Regex StrikeRx = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
    private static readonly Regex ItalicRx = new(@"(?<![\*\w])\*(?=[^\s\*])(.+?)(?<=[^\s\*])\*(?![\*\w])|(?<![\w_])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![\w_])", RegexOptions.Compiled);

    private static readonly string[] SafeSchemes = ["http", "https", "mailto"];

    /// <summary>Convert Markdown to Confluence storage format. Empty/whitespace input yields an empty string.</summary>
    public static string FromMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        var sb = new StringBuilder();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Fenced code block.
            var fence = FenceRx.Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                var language = fence.Groups[2].Value;
                var code = new List<string>();
                i++;
                while (i < lines.Length && !IsClosingFence(lines[i], marker))
                {
                    code.Add(lines[i]);
                    i++;
                }

                i++; // closing fence (or end of input for an unterminated block)
                sb.Append(CodeMacro(string.Join("\n", code), language));
                continue;
            }

            var heading = HeadingRx.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                sb.Append($"<h{level}>{Inline(heading.Groups[2].Value)}</h{level}>");
                i++;
                continue;
            }

            if (RuleRx.IsMatch(line))
            {
                sb.Append("<hr/>");
                i++;
                continue;
            }

            // Table: a header row followed by a separator row.
            if (line.Contains('|') && i + 1 < lines.Length && lines[i + 1].Contains('-') && TableSepRx.IsMatch(lines[i + 1]))
            {
                var rows = new List<string> { line };
                i += 2;
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|'))
                {
                    rows.Add(lines[i]);
                    i++;
                }

                sb.Append(RenderTable(rows));
                continue;
            }

            if (QuoteRx.IsMatch(line))
            {
                var quoted = new List<string>();
                while (i < lines.Length && QuoteRx.Match(lines[i]) is { Success: true } q)
                {
                    quoted.Add(q.Groups[1].Value);
                    i++;
                }

                // Quote content may itself be blocks (lists, code, ...): convert it recursively.
                sb.Append("<blockquote>").Append(FromMarkdown(string.Join("\n", quoted))).Append("</blockquote>");
                continue;
            }

            if (ListRx.IsMatch(line))
            {
                var items = new List<ListEntry>();
                while (i < lines.Length && ListRx.Match(lines[i]) is { Success: true } m)
                {
                    var marker = m.Groups[2].Value;
                    items.Add(new ListEntry(m.Groups[1].Length, char.IsDigit(marker[0]), m.Groups[3].Value));
                    i++;
                }

                var idx = 0;
                while (idx < items.Count)
                {
                    sb.Append(RenderList(items, ref idx));
                }

                continue;
            }

            // Paragraph: consecutive plain lines; single newlines become <br/>.
            var para = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines[i], para.Count > 0))
            {
                para.Add(lines[i].Trim());
                i++;
            }

            if (para.Count == 0)
            {
                // Defensive: never loop forever on a line that no rule claimed.
                para.Add(lines[i].Trim());
                i++;
            }

            sb.Append("<p>").Append(string.Join("<br/>", para.Select(Inline))).Append("</p>");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Blocks
    // ------------------------------------------------------------------
    private static bool IsClosingFence(string line, string marker)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= marker.Length && trimmed.All(c => c == marker[0]);
    }

    private static bool StartsBlock(string line, bool insideParagraph) =>
        FenceRx.IsMatch(line) || HeadingRx.IsMatch(line) || RuleRx.IsMatch(line) ||
        QuoteRx.IsMatch(line) || (insideParagraph && ListRx.IsMatch(line));

    private static string CodeMacro(string code, string language)
    {
        var sb = new StringBuilder("<ac:structured-macro ac:name=\"code\" ac:schema-version=\"1\">");
        if (language.Length > 0)
        {
            sb.Append("<ac:parameter ac:name=\"language\">").Append(Escape(language)).Append("</ac:parameter>");
        }

        // A literal "]]>" would end the CDATA section early; split it across two sections.
        sb.Append("<ac:plain-text-body><![CDATA[").Append(code.Replace("]]>", "]]]]><![CDATA[>")).Append("]]></ac:plain-text-body>");
        sb.Append("</ac:structured-macro>");
        return sb.ToString();
    }

    private sealed record ListEntry(int Indent, bool Ordered, string Text);

    private static string RenderList(List<ListEntry> items, ref int i)
    {
        var baseIndent = items[i].Indent;
        var ordered = items[i].Ordered;
        var tag = ordered ? "ol" : "ul";
        var sb = new StringBuilder($"<{tag}>");

        while (i < items.Count && items[i].Indent >= baseIndent && items[i].Ordered == ordered)
        {
            var entry = items[i++];
            sb.Append("<li>").Append(Inline(entry.Text));
            if (i < items.Count && items[i].Indent > baseIndent)
            {
                sb.Append(RenderList(items, ref i));
            }

            sb.Append("</li>");
        }

        return sb.Append($"</{tag}>").ToString();
    }

    private static string RenderTable(List<string> rows)
    {
        var sb = new StringBuilder("<table><tbody>");
        for (var r = 0; r < rows.Count; r++)
        {
            var cellTag = r == 0 ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in SplitRow(rows[r]))
            {
                sb.Append($"<{cellTag}><p>").Append(Inline(cell)).Append($"</p></{cellTag}>");
            }

            sb.Append("</tr>");
        }

        return sb.Append("</tbody></table>").ToString();
    }

    /// <summary>Split a table row on unescaped pipes, dropping the optional leading/trailing pipe.</summary>
    private static List<string> SplitRow(string row)
    {
        var trimmed = row.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|"))
        {
            trimmed = trimmed[..^1];
        }

        var cells = new List<string>();
        var current = new StringBuilder();
        for (var k = 0; k < trimmed.Length; k++)
        {
            if (trimmed[k] == '\\' && k + 1 < trimmed.Length && trimmed[k + 1] == '|')
            {
                current.Append('|');
                k++;
            }
            else if (trimmed[k] == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(trimmed[k]);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    // ------------------------------------------------------------------
    // Inline
    // ------------------------------------------------------------------
    /// <summary>Convert inline Markdown to storage-format XHTML; all literal text is XML-escaped.</summary>
    public static string Inline(string text)
    {
        var sb = new StringBuilder();
        var last = 0;

        // Code spans are opaque: nothing inside them is interpreted.
        foreach (Match code in CodeSpanRx.Matches(text))
        {
            sb.Append(InlineLinks(text[last..code.Index]));
            sb.Append("<code>").Append(Escape(code.Groups[2].Value.Trim())).Append("</code>");
            last = code.Index + code.Length;
        }

        sb.Append(InlineLinks(text[last..]));
        return sb.ToString();
    }

    private static string InlineLinks(string text)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match link in LinkRx.Matches(text))
        {
            sb.Append(Emphasis(text[last..link.Index]));
            var label = Emphasis(link.Groups[1].Value);
            var url = link.Groups[2].Value;
            if (IsSafeUrl(url))
            {
                sb.Append("<a href=\"").Append(Escape(url)).Append("\">").Append(label).Append("</a>");
            }
            else
            {
                sb.Append(label); // drop unsafe schemes (javascript:, data:, ...) but keep the text
            }

            last = link.Index + link.Length;
        }

        sb.Append(Emphasis(text[last..]));
        return sb.ToString();
    }

    private static string Emphasis(string text)
    {
        if (text.Length == 0)
        {
            return "";
        }

        var escaped = Escape(text);
        escaped = BoldRx.Replace(escaped, m => "<strong>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</strong>");
        escaped = StrikeRx.Replace(escaped, "<del>$1</del>");
        escaped = ItalicRx.Replace(escaped, m => "<em>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</em>");
        return escaped;
    }

    /// <summary>Escape the five XML-significant characters; everything else (incl. non-ASCII) passes through.</summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static bool IsSafeUrl(string url)
    {
        if (url.StartsWith('#') || url.StartsWith('/'))
        {
            return true;
        }

        var colon = url.IndexOf(':');
        if (colon < 0)
        {
            return true; // relative reference
        }

        return SafeSchemes.Contains(url[..colon], StringComparer.OrdinalIgnoreCase);
    }
}
