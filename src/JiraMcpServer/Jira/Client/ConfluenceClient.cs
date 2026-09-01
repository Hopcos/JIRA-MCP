using System.Text.Json;
using System.Text.Json.Nodes;
using JiraMcpServer.Jira.Formatters;

namespace JiraMcpServer.Jira.Client;

/// <summary>
/// HTTP client for the Confluence Cloud REST API (the classic <c>/wiki/rest/api</c> surface),
/// reusing the authenticated request pipeline of <see cref="JiraClient"/>. Confluence lives on the
/// same Atlassian instance as Jira (under the <c>/wiki</c> path), so it shares the server's
/// <c>base_url</c>, credentials, token-bucket rate limiter, retry/backoff, and per-request token
/// masking. Only read operations are exposed; Confluence writes are out of scope for this server.
/// </summary>
public sealed class ConfluenceClient
{
    private readonly JiraClient _jira;

    public ConfluenceClient(JiraClient jira)
    {
        _jira = jira;
    }

    public const int PaginationLimit = 50;
    public const int MaxBodyChars = 60_000;

    /// <summary>
    /// Fetch a Confluence page (classic API) with its body as ADF. Returns a compact JSON object
    /// (<c>id</c>, <c>title</c>, <c>spaceKey</c>, <c>url</c>, <c>version</c>, <c>authorId</c>,
    /// <c>createdAt</c>, <c>body_text</c>, <c>body_html</c>) so the model gets readable text plus a
    /// pragmatic HTML fallback. False when the page exists but has no body.
    /// </summary>
    public async Task<(JsonObject? Data, bool HasBody)> GetPageAsync(string pageId, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["expand"] = "body.storage,body.atlas_doc_format,version,space",
        };

        using var response = await _jira.RequestAsync(HttpMethod.Get, $"/wiki/rest/api/content/{pageId}", query, cancellationToken: cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return BuildPage(doc.RootElement);
    }

    /// <summary>
    /// Search Confluence with CQL. Returns <c>{"results": [...], "total": n, "limit": l, "start": s}</c>
    /// with summaries for each hit (id/title/spaceKey/type/url). Include the body via <paramref name="includeBody"/>.
    /// </summary>
    public async Task<JsonObject> SearchAsync(
        string cql,
        int limit,
        int start,
        bool includeBody,
        bool includePlainText,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, PaginationLimit);
        var query = new Dictionary<string, string?>
        {
            ["cql"] = cql,
            ["limit"] = capped.ToString(),
            ["start"] = Math.Max(0, start).ToString(),
        };

        // The classic search endpoint caps expanded bodies at 50 results regardless of limit.
        var expand = new List<string>();
        if (includeBody)
        {
            expand.Add("body.storage");
        }
        else if (includePlainText)
        {
            expand.Add("body.storage");
        }

        if (expand.Count > 0)
        {
            query["expand"] = string.Join(",", expand);
        }

        using var response = await _jira.RequestAsync(HttpMethod.Get, "/wiki/rest/api/content/search", query, cancellationToken: cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var compact = new JsonObject
        {
            ["cql"] = cql,
            ["limit"] = capped,
            ["start"] = Math.Max(0, start),
            ["total"] = root.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
            ["results"] = new JsonArray(),
        };

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var item in results.EnumerateArray())
            {
                array.Add(BuildSearchItem(item, includeBody));
            }

            compact["results"] = array;
        }

        return compact;
    }

    /// <summary>List spaces (classic API), compacted to id/key/name/type.</summary>
    public async Task<JsonObject> ListSpacesAsync(int limit, int start, CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, PaginationLimit);
        var query = new Dictionary<string, string?>
        {
            ["limit"] = capped.ToString(),
            ["start"] = Math.Max(0, start).ToString(),
        };

        using var response = await _jira.RequestAsync(HttpMethod.Get, "/wiki/rest/api/space", query, cancellationToken: cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        var spaces = new JsonArray();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                spaces.Add(new JsonObject
                {
                    ["id"] = SafeString(item, "id"),
                    ["key"] = SafeString(item, "key"),
                    ["name"] = SafeString(item, "name"),
                    ["type"] = SafeString(item, "type"),
                });
            }
        }

        return new JsonObject
        {
            ["limit"] = capped,
            ["start"] = Math.Max(0, start),
            ["total"] = root.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : spaces.Count,
            ["results"] = spaces,
        };
    }

    /// <summary>Get a single space by key.</summary>
    public async Task<JsonObject> GetSpaceAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        using var response = await _jira.RequestAsync(HttpMethod.Get, $"/wiki/rest/api/space/{spaceKey}", cancellationToken: cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        return new JsonObject
        {
            ["key"] = root.TryGetProperty("key", out var key) ? key.GetString() : spaceKey,
            ["name"] = SafeString(root, "name"),
            ["type"] = SafeString(root, "type"),
            ["id"] = SafeString(root, "id"),
        };
    }

    // ------------------------------------------------------------------
    // Body rendering
    // ------------------------------------------------------------------
    private static (JsonObject? Data, bool HasBody) BuildPage(JsonElement root)
    {
        var obj = new JsonObject
        {
            ["id"] = SafeString(root, "id"),
            ["title"] = SafeString(root, "title"),
            ["type"] = SafeString(root, "type"),
            ["status"] = SafeString(root, "status"),
            ["spaceKey"] = root.TryGetProperty("space", out var space) && space.ValueKind == JsonValueKind.Object
                ? SafeString(space, "key")
                : null,
            ["authorId"] = SafeString(root, "lastUpdated", "by", "accountId") ?? SafeString(root, "version", "by", "accountId"),
            ["createdAt"] = SafeString(root, "createdAt"),
            ["versionNumber"] = root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object
                ? (version.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt64() : (long?)null)
                : null,
        };

        if (root.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            var baseUrl = links.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var webui = links.TryGetProperty("webui", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                obj["url"] = baseUrl.TrimEnd('/') + webui;
            }
            else if (!string.IsNullOrEmpty(webui))
            {
                obj["url"] = webui;
            }
        }

        var (hasBody, text, html) = ExtractBody(root);
        obj["body_text"] = text;
        obj["body_html"] = html ?? "";
        obj["has_body"] = hasBody;

        return (obj, hasBody);
    }

    /// <summary>
    /// Extract a readable body from a page payload. The classic API returns every representation
    /// as <c>{value: "&lt;string&gt;", representation: ...}</c>: <c>body.atlas_doc_format.value</c> is a
    /// JSON-stringified ADF document (best fidelity), while <c>body.storage.value</c> is HTML
    /// (converted with <see cref="HtmlToText"/>). ADF wins when parseable; HTML is the fallback.
    /// </summary>
    private static (bool HasBody, string Text, string? Html) ExtractBody(JsonElement root)
    {
        if (!root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
        {
            return (false, "", null);
        }

        // 1) atlas_doc_format: a JSON-string ADF document.
        if (body.TryGetProperty("atlas_doc_format", out var adfObj) && adfObj.ValueKind == JsonValueKind.Object &&
            adfObj.TryGetProperty("value", out var adfValue) && adfValue.ValueKind == JsonValueKind.String)
        {
            var adfText = AdfJsonStringToText(adfValue.GetString());
            if (!string.IsNullOrWhiteSpace(adfText))
            {
                return (true, Cap(adfText), null);
            }
        }

        // 2) storage: HTML. Keep the HTML as a pragmatic fallback for the model.
        if (body.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object &&
            storage.TryGetProperty("value", out var storageValue) && storageValue.ValueKind == JsonValueKind.String)
        {
            var html = storageValue.GetString() ?? "";
            var text = HtmlToText.Convert(html);
            return (text.Length > 0 || html.Length > 0,
                Cap(text.Length > 0 ? text : html),
                Cap(html));
        }

        return (false, "", null);
    }

    /// <summary>Render a JSON-string ADF document (from <c>atlas_doc_format.value</c>) to plain text.</summary>
    private static string AdfJsonStringToText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        try
        {
            return ConfluenceAdf.AdfToText(JsonNode.Parse(raw)) ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Build a compact search hit: id/title/spaceKey/type/url plus optional body fields.</summary>
    private static JsonObject BuildSearchItem(JsonElement item, bool includeBody)
    {
        var hit = new JsonObject
        {
            ["id"] = SafeString(item, "id"),
            ["type"] = SafeString(item, "type"),
            ["title"] = SafeString(item, "title"),
            ["spaceKey"] = item.TryGetProperty("space", out var space) && space.ValueKind == JsonValueKind.Object
                ? SafeString(space, "key")
                : null,
        };

        if (item.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            var baseUrl = links.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var webui = links.TryGetProperty("webui", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                hit["url"] = baseUrl.TrimEnd('/') + webui;
            }
            else if (!string.IsNullOrEmpty(webui))
            {
                hit["url"] = webui;
            }
        }

        if (includeBody && item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            // The classic search endpoint returns body.storage as HTML (not ADF).
            if (body.TryGetProperty("storage", out var storage) && storage.ValueKind == JsonValueKind.Object &&
                storage.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var html = value.GetString() ?? "";
                var text = HtmlToText.Convert(html);
                hit["excerpt"] = Cap(text.Length > 0 ? text : html, 4_000);
            }
        }

        return hit;
    }

    private static string? SafeString(JsonElement obj, params string[] path)
    {
        JsonElement current = obj;
        foreach (var key in path)
        {
            if (!current.TryGetProperty(key, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static string Cap(string text, int maxChars = MaxBodyChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars] + $"\n[… truncated at {maxChars} chars]";
    }
}
