using System.Text.RegularExpressions;

namespace JiraMcpServer.Jira.Validators;

/// <summary>
/// Parses Confluence page URLs (short or full) into their page id and space parts, so tools can
/// accept the exact URL a user pastes. Recognized shapes, matched in order:
/// <list type="bullet">
///   <item>/wiki/spaces/&lt;spaceKey&gt;/pages/&lt;pageId&gt;/&lt;title&gt;</item>
///   <item>/wiki/spaces/&lt;spaceKey&gt;/pages/&lt;pageId&gt;</item>
///   <item>/wiki/pages/viewpage.action?pageId=&lt;pageId&gt;</item>
///   <item>/pages/viewpage.action?pageId=&lt;pageId&gt;</item>
///   <item>/wiki/spaces/&lt;spaceKey&gt;/overview</item>
///   <item>bare Confluence page ids (all digits)</item>
/// </list>
/// </summary>
public static class ConfluenceUrl
{
    private static readonly Regex[] Patterns =
    [
        // https://everymatrix.atlassian.net/wiki/spaces/PE/pages/5233541311/Basic+information
        //                                optional prefix         space    pageId      optional title
        new(
            @"(?<space>/wiki/spaces/[^/]+/pages/(?<id>\d+))(?<title>/[^?#]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(
            @"(?<space>/wiki/spaces/[^/]+/overview)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(
            @"/viewpage\.action\?[^#]*\bpageId=(?<id>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private static readonly Regex BareId = new(@"^\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Structural parse of a Confluence page/space URL or bare page id. Returns an empty result
    /// (never null) when the input is not recognized, so callers can fall back gracefully.
    /// </summary>
    public static ConfluenceUrlParts TryParse(string? input)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return ConfluenceUrlParts.Empty;
        }

        // A bare numeric page id is the common case from tooling / pasted id.
        if (BareId.IsMatch(trimmed))
        {
            return new ConfluenceUrlParts { PageId = trimmed };
        }

        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            var parts = new ConfluenceUrlParts();

            var pageIdGroup = match.Groups["id"];
            if (pageIdGroup.Success)
            {
                parts.PageId = pageIdGroup.Value;
            }

            var spaceGroup = match.Groups["space"];
            if (spaceGroup.Success)
            {
                var tokens = spaceGroup.Value.Split('/');
                // tokens = ["", "wiki", "spaces", "<key>", "pages", "<id>"(optional from group 1)]
                var spaceIndex = Array.IndexOf(tokens, "spaces");
                if (spaceIndex >= 0 && spaceIndex + 1 < tokens.Length)
                {
                    parts.SpaceKey = tokens[spaceIndex + 1];
                }
            }

            var titleGroup = match.Groups["title"];
            if (titleGroup.Success)
            {
                // Strip the leading slash and decode the URL-safe title (pluses are spaces).
                var raw = titleGroup.Value.TrimStart('/');
                parts.PageTitle = Uri.UnescapeDataString(raw.Replace("+", "%20"));
            }

            if (parts.PageId is not null || parts.SpaceKey is not null)
            {
                return parts;
            }
        }

        return ConfluenceUrlParts.Empty;
    }

    /// <summary>Uppercase a space key for stable comparisons.</summary>
    public static string NormalizeSpaceKey(string? key) => key?.Trim().ToUpperInvariant() ?? "";
}

/// <summary>Structured result of an URL parse: at most one of the parts is populated.</summary>
public sealed record ConfluenceUrlParts
{
    public static readonly ConfluenceUrlParts Empty = new();

    /// <summary>Confluence numeric content/page id, when the URL (or a bare id) carried one.</summary>
    public string? PageId { get; set; }

    /// <summary>Confluence space key (uppercased), when the URL carried a /spaces/&lt;key&gt; segment.</summary>
    public string? SpaceKey { get; set; }

    /// <summary>URL title segment, decoded; informational only (not used for lookups).</summary>
    public string? PageTitle { get; set; }

    public bool HasPageId => !string.IsNullOrEmpty(PageId);

    public bool HasSpaceKey => !string.IsNullOrEmpty(SpaceKey);
}
