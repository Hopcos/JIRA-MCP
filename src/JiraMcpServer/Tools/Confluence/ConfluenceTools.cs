using System.ComponentModel;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Jira.Errors;
using JiraMcpServer.Jira.Formatters;
using JiraMcpServer.Jira.Validators;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Confluence;

/// <summary>
/// Confluence Cloud tools: page lookup (by id/URL/title across the wiki), CQL search,
/// space listing/detail, plus page creation and content writes (create/update). All requests
/// share the Jira client's credentials, rate limiter, and retry pipeline through
/// <see cref="ConfluenceClient"/> (injected via <see cref="JiraToolContext"/>). The write tools
/// are categorized as create/update in <c>Permissions</c>, so a <c>read</c>-scoped
/// <c>JIRA_TOOLS</c> deployment never exposes them; page deletion is not offered.
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

    [McpServerTool(Name = "confluence_create_page", Title = "Create a Confluence page")]
    [Description("Create a new Confluence page in a space and write its content. The content is Markdown by default (headings, lists, tables, fenced code, bold/italic, links are converted to Confluence format); pass body_format='storage' to supply raw Confluence storage-format XHTML instead. Optionally nests the page under a parent page. Fails if a page with the same title already exists in the space.")]
    public async Task<CallToolResult> CreatePageAsync(
        [Description("Space key the page is created in, e.g. PE")] string spaceKey,
        [Description("Page title (must be unique within the space)")] string title,
        [Description("Page content in the format given by body_format; may be empty to create a blank page")] string body = "",
        [Description("Optional parent page id or full page URL to nest the new page under; omitted = top level of the space")] string? parentId = null,
        [Description("Content format: 'markdown' (default) or 'storage' (raw Confluence storage-format XHTML)")] string bodyFormat = "markdown",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var space = ConfluenceUrl.NormalizeSpaceKey(spaceKey);
            if (space.Length == 0)
            {
                return ToolResult.ErrorResult(new JiraValidationError("space_key must not be empty", statusCode: 400));
            }

            var pageTitle = title?.Trim() ?? "";
            if (pageTitle.Length == 0)
            {
                return ToolResult.ErrorResult(new JiraValidationError("title must not be empty", statusCode: 400));
            }

            string? parent = null;
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                var parsedParent = ConfluenceUrl.TryParse(parentId);
                if (!parsedParent.HasPageId)
                {
                    return ToolResult.ErrorResult(new JiraValidationError(
                        "parent_id must be a numeric page id or a full page URL containing one", statusCode: 400));
                }

                parent = parsedParent.PageId;
            }

            var storage = ToStorage(body, bodyFormat);
            var result = await Confluence.CreatePageAsync(space, pageTitle, storage, parent, cancellationToken);
            EnsureUrl(result);
            result["created"] = true;
            return ToolResult.DictResult(result, "page");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "confluence_update_page", Title = "Write content to a Confluence page")]
    [Description("Write content to an existing Confluence page, replacing the whole body (mode='replace', default) or adding to the end of it (mode='append'). The page version is incremented automatically. The content is Markdown by default; pass body_format='storage' for raw Confluence storage-format XHTML. Optionally renames the page. In replace mode the previous body is overwritten (it stays available in the page history).")]
    public async Task<CallToolResult> UpdatePageAsync(
        [Description("Confluence page id (numeric) or a full page URL")] string pageIdOrUrl,
        [Description("New content in the format given by body_format; must not be empty")] string body,
        [Description("Write mode: 'replace' (default) overwrites the page body, 'append' adds the content after the existing body")] string mode = "replace",
        [Description("Optional new page title; omitted = keep the current title")] string? title = null,
        [Description("Content format: 'markdown' (default) or 'storage' (raw Confluence storage-format XHTML)")] string bodyFormat = "markdown",
        [Description("Optional version comment shown in the page history")] string? versionMessage = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = ConfluenceUrl.TryParse(pageIdOrUrl);
            if (!parsed.HasPageId)
            {
                return ToolResult.ErrorResult(new JiraValidationError(
                    "page_id_or_url must be a numeric page id or a full page URL containing one; use confluence_search to find the id of a page by title",
                    statusCode: 400));
            }

            var normalizedMode = mode?.Trim().ToLowerInvariant();
            if (normalizedMode is not ("replace" or "append"))
            {
                return ToolResult.ErrorResult(new JiraValidationError("mode must be 'replace' or 'append'", statusCode: 400));
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return ToolResult.ErrorResult(new JiraValidationError(
                    "body must not be empty (an empty update would erase the page content)", statusCode: 400));
            }

            var storage = ToStorage(body, bodyFormat);
            var result = await Confluence.UpdatePageAsync(
                parsed.PageId!,
                storage,
                title,
                append: normalizedMode == "append",
                versionMessage,
                cancellationToken);
            EnsureUrl(result);
            result["updated"] = true;
            return ToolResult.DictResult(result, "page");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    /// <summary>Convert tool input to storage format according to <paramref name="bodyFormat"/>.</summary>
    private static string ToStorage(string? body, string? bodyFormat)
    {
        return (bodyFormat?.Trim().ToLowerInvariant()) switch
        {
            null or "" or "markdown" or "md" => ConfluenceStorage.FromMarkdown(body),
            "storage" => body ?? "",
            _ => throw new JiraValidationError("body_format must be 'markdown' or 'storage'", statusCode: 400),
        };
    }

    /// <summary>Fill in the page URL from the configured base URL when Confluence did not return one.</summary>
    private void EnsureUrl(System.Text.Json.Nodes.JsonObject page)
    {
        if (!string.IsNullOrEmpty(ToolPayload.GetString(page, "url")))
        {
            return;
        }

        var id = ToolPayload.GetString(page, "id");
        var space = ToolPayload.GetString(page, "spaceKey");
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(space))
        {
            page["url"] = $"{_ctx.Settings.JiraBaseUrl}/wiki/spaces/{space}/pages/{id}";
        }
    }

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
