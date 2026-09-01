using System.ComponentModel;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Jira.Errors;
using JiraMcpServer.Jira.Validators;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Confluence;

/// <summary>
/// Confluence Cloud read tools: page lookup (by id/URL/title across the wiki), CQL search,
/// space listing/detail. All requests share the Jira client's credentials, rate limiter, and
/// retry pipeline through <see cref="ConfluenceClient"/> (injected via
/// <see cref="JiraToolContext"/>); only read operations are exposed.
/// </summary>
[McpServerToolType]
public sealed class ConfluenceTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    private ConfluenceClient Confluence => _ctx.Confluence;

    [McpServerTool(Name = "confluence_get_page", Title = "Get a Confluence page")]
    [Description("Fetch a Confluence page by its page id, a full wiki page URL (e.g. https://your-domain.atlassian.net/wiki/spaces/KEY/pages/12345/Title), or a title query. Recreates the page URL for the configured base_url when given a bare id.")]
    public async Task<CallToolResult> GetPageAsync(
        [Description("Confluence page id (numeric), a full page URL, or a title to search by prefix")] string pageIdOrUrl,
        [Description("When true, interpret the input as a title to search for (requires space_key)")] bool searchByTitle = false,
        [Description("Space key (e.g. PE) to scope a title search, or to build the page URL when only an id is given")] string? spaceKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var raw = pageIdOrUrl.Trim();
            if (raw.Length == 0)
            {
                return ToolResult.ErrorResult(new JiraValidationError("page_id_or_url must not be empty", statusCode: 400));
            }

            var parsed = ConfluenceUrl.TryParse(raw);
            string? pageId = null;
            var parsedSpace = parsed.SpaceKey;

            if (searchByTitle)
            {
                var space = ConfluenceUrl.NormalizeSpaceKey(spaceKey ?? parsedSpace);
                if (space.Length == 0)
                {
                    return ToolResult.ErrorResult(
                        new JiraValidationError(
                            "When search_by_title=true, space_key must be provided (or present in the URL) to scope the title search",
                            statusCode: 400));
                }

                pageId = await ResolveTitleToPageIdAsync(raw, space, cancellationToken);
                if (pageId is null)
                {
                    return ToolResult.DictResult(new
                    {
                        found = false,
                        title = raw,
                        space,
                        note = $"No page with title '{raw}' in space {space}",
                    }, "page");
                }
            }
            else if (parsed.HasPageId)
            {
                pageId = parsed.PageId;
            }
            else if (parsed.SpaceKey is not null || spaceKey is not null)
            {
                // A URL (or a space-qualified title) we could not extract a page id from: try a
                // title lookup in the space the URL names.
                var space = ConfluenceUrl.NormalizeSpaceKey(parsed.SpaceKey ?? spaceKey);
                var titleFromUrl = LastUrlSegment(raw);
                if (!string.IsNullOrEmpty(titleFromUrl) && space.Length > 0)
                {
                    pageId = await ResolveTitleToPageIdAsync(titleFromUrl, space, cancellationToken);
                    if (pageId is null)
                    {
                        return ToolResult.DictResult(new
                        {
                            found = false,
                            title = titleFromUrl,
                            space,
                            note = "No page with that title was found; pass the numeric page id from the URL instead",
                        }, "page");
                    }
                }
            }

            if (pageId is null)
            {
                return ToolResult.DictResult(new
                {
                    found = false,
                    note = "Could not resolve a page id from the input. Pass a numeric page id, a full page URL, or search_by_title=true with a space_key.",
                }, "page");
            }

            var (data, hasBody) = await Confluence.GetPageAsync(pageId, cancellationToken);
            if (data is null)
            {
                throw new JiraNotFoundError($"Confluence page {pageId} was not found or could not be parsed", statusCode: 404);
            }

            // Default the space key and reconstruct the canonical URL when Confluence did not
            // return one (the classic API does not always populate _links on the single fetch).
            if (string.IsNullOrEmpty(ToolPayload.GetString(data, "url")))
            {
                var space = parsedSpace ?? ConfluenceUrl.NormalizeSpaceKey(spaceKey);
                if (space.Length == 0)
                {
                    space = ToolPayload.GetString(data, "spaceKey") ?? "";
                }

                if (space.Length > 0)
                {
                    data["url"] = $"{_ctx.Settings.JiraBaseUrl}/wiki/spaces/{space}/pages/{pageId}";
                }
            }

            return ToolResult.DictResult(data, "page");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "confluence_search", Title = "Search Confluence pages with CQL")]
    [Description("Search Confluence content using Confluence Query Language (CQL), e.g. space = 'PE' AND text ~ 'bonus'. Use the `~` operator for 'contains/prefix' text matching. Returns page id, title, space key, and url for each hit.")]
    public async Task<CallToolResult> SearchAsync(
        [Description("CQL query, e.g. space = 'PE' AND text ~ 'bonus requirements'")] string cql,
        [Description("Maximum number of results (1-50)")] int limit = 25,
        [Description("Starting index for pagination")] int start = 0,
        [Description("Include a short body excerpt for each hit")] bool includeBody = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await Confluence.SearchAsync(cql, limit, start, includeBody, includePlainText: true, cancellationToken);
            if (data is System.Text.Json.Nodes.JsonObject payload)
            {
                var count = (payload["results"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
                payload["note"] = count == 0
                    ? "No matches. Try fewer or less specific terms, or check the space key in the CQL."
                    : "For the full body of any hit, call confluence_get_page with its page id.";
            }

            return ToolResult.DictResult(data, "search");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "confluence_list_spaces", Title = "List Confluence spaces")]
    [Description("Return Confluence spaces the authenticated user can view, compacted to key, name, and type.")]
    public async Task<CallToolResult> ListSpacesAsync(
        [Description("Maximum number of spaces to return")] int limit = 50,
        [Description("Starting index for pagination")] int start = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await Confluence.ListSpacesAsync(limit, start, cancellationToken);
            return ToolResult.DictResult(data, "spaces");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "confluence_get_space", Title = "Get a Confluence space")]
    [Description("Fetch a single Confluence space by its key (e.g. PE).")]
    public async Task<CallToolResult> GetSpaceAsync(
        [Description("Space key, e.g. PE")] string spaceKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var key = ConfluenceUrl.NormalizeSpaceKey(spaceKey);
            if (key.Length == 0)
            {
                return ToolResult.ErrorResult(new JiraValidationError("space_key must not be empty", statusCode: 400));
            }

            var data = await Confluence.GetSpaceAsync(key, cancellationToken);
            return ToolResult.DictResult(data, "space");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    /// <summary>Resolve a page id by searching for an exact (falling back to prefix) title in a space.</summary>
    private async Task<string?> ResolveTitleToPageIdAsync(string title, string spaceKey, CancellationToken cancellationToken)
    {
        var cql = Cql.Build(new[]
        {
            "type = page",
            $"space = {Cql.EscapeValue(spaceKey)}",
            $"title ~ {Cql.EscapeValue(title)}",
        });

        var data = await Confluence.SearchAsync(cql, limit: 5, start: 0, includeBody: false, includePlainText: false, cancellationToken);
        var results = (data["results"] as System.Text.Json.Nodes.JsonArray) ?? new System.Text.Json.Nodes.JsonArray();

        // Prefer an exact (case-insensitive) title match.
        foreach (var result in results.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (string.Equals(ToolPayload.GetString(result, "title"), title, StringComparison.OrdinalIgnoreCase))
            {
                return ToolPayload.GetString(result, "id");
            }
        }

        // Fall back to the first page hit.
        var first = results.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault();
        return first is null ? null : ToolPayload.GetString(first, "id");
    }

    private static string? LastUrlSegment(string input)
    {
        var trimmed = input.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var segment = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return Uri.UnescapeDataString(segment.Replace("+", "%20"));
    }
}
