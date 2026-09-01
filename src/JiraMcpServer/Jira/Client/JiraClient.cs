using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Errors;
using JiraMcpServer.Jira.Safety;
using JiraMcpServer.Jira.Validators;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Jira.Client;

/// <summary>Constants shared by the Jira client and its callers.</summary>
public static class JiraDefaults
{
    public const int DefaultMaxResults = 50;
    public const int PaginationLimit = 100;

    /// <summary>
    /// Field names requested when a search does not ask for fields explicitly. The enhanced
    /// <c>/search/jql</c> endpoint returns bare <c>{"id": ...}</c> records unless a fields array
    /// (or expand) is sent; requesting this set keeps results useful without a full-fields fetch.
    /// </summary>
    public static readonly string[] DefaultSearchFields = ["summary", "status", "assignee"];

    /// <summary>Maximum attachment upload size, matching Jira Cloud's 10 MiB limit.</summary>
    public const long MaxAttachmentBytes = 10 * 1024 * 1024;
}

/// <summary>
/// HTTP client for the Jira REST and Agile APIs with client-side rate limiting, automatic retry
/// with exponential backoff, pagination, project-whitelist enforcement, and per-request credential
/// refresh (rotating environment variables takes effect without a restart).
/// </summary>
public sealed class JiraClient : IDisposable
{
    private static readonly double[] RetriableBackoffSeconds = [1.0, 2.0, 4.0];

    private readonly HttpClient _http;
    private readonly ILogger<JiraClient> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly bool _ownsClient;
    private bool _disposed;

    public JiraClient(
        CompiledConfig settings,
        HttpClient? httpClient = null,
        ILogger<JiraClient>? logger = null,
        int maxRetries = 3)
    {
        Settings = settings;
        MaxRetries = Math.Max(0, maxRetries);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<JiraClient>.Instance;

        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = settings.RateLimit,
            TokensPerPeriod = settings.RateLimit,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 100,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(settings.ConnectTimeout),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                EnableMultipleHttp2Connections = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                BaseAddress = new Uri(settings.JiraBaseUrl),
                Timeout = TimeSpan.FromSeconds(settings.RequestTimeout),
                DefaultRequestHeaders = { Accept = { new MediaTypeWithQualityHeaderValue("application/json") } },
            };
            _ownsClient = true;
        }
    }

    public CompiledConfig Settings { get; }

    public int MaxRetries { get; }

    /// <summary>
    /// The underlying <see cref="HttpClient"/>, exposed so sibling clients (e.g.
    /// <see cref="ConfluenceClient"/>) can share the same BaseAddress, auth headers, and handler
    /// without duplicating the transport pipeline. The caller must not dispose it; this client
    /// owns it.
    /// </summary>
    public HttpClient Http => _http;

    /// <summary>
    /// Build the Authorization header from the live settings on every call, so a token rotation
    /// in the environment takes effect on the next request without a restart.
    /// </summary>
    private AuthenticationHeaderValue CurrentAuthHeader()
    {
        if (Settings.AuthMethod == "bearer")
        {
            return new AuthenticationHeaderValue("Bearer", Settings.ApiToken);
        }

        // basic
        var raw = $"{Settings.UserEmail}:{Settings.ApiToken}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        return new AuthenticationHeaderValue("Basic", encoded);
    }

    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("Rate limiter did not grant a lease within the queue limit.");
        }
    }

    private static DateTimeOffset? ParseRetryAfter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds));
    }

    private static string SafeErrorText(HttpResponseMessage response)
    {
        // Read the body once into a string: a decompressed HttpContent stream can only be read
        // once, so parsing JSON and then falling back to the raw text must both work from that
        // single read rather than calling ReadAsStream()/ReadAsStringAsync() twice.
        string body;
        try
        {
            body = response.Content.ReadAsStringAsync().Result;
        }
        catch (Exception)
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var messages = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "errorMessages", "error" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var value))
                    {
                        switch (value.ValueKind)
                        {
                            case JsonValueKind.String:
                                messages.Add(value.GetString() ?? "");
                                break;
                            case JsonValueKind.Array:
                                messages.AddRange(value.EnumerateArray().Select(static e => e.ToString()));
                                break;
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Object)
                {
                    messages.AddRange(errors.EnumerateObject()
                        .Where(static p => p.Value.ToString().Length > 0)
                        .Select(static p => $"{p.Name}: {p.Value}"));
                }
            }

            if (messages.Count > 0)
            {
                return string.Join(" | ", messages.Where(static m => m.Length > 0));
            }
        }
        catch (Exception)
        {
            // Not JSON; fall through to the body text.
        }

        return string.IsNullOrWhiteSpace(body) ? "" : body[..Math.Min(300, body.Length)];
    }

    private static JiraError MapToJiraError(HttpResponseMessage response, int statusCode)
    {
        var text = SafeErrorText(response);
        return statusCode switch
        {
            401 => new JiraAuthenticationError(text.Length > 0 ? text : "Authentication failed (401)", statusCode),
            403 => new JiraPermissionError(
                text.Length > 0 ? text : "Permission denied (403)",
                statusCode,
                warnings: ["You may need a different token or permission."]),
            404 => new JiraNotFoundError(text.Length > 0 ? text : "Resource not found (404)", statusCode),
            429 => new JiraRateLimitError(
                text.Length > 0 ? text : "Rate limited (429)",
                statusCode,
                retryAfter: ParseRetryAfter(response.Headers.RetryAfter?.ToString()) is { } retry
                    ? (retry - DateTimeOffset.UtcNow).TotalSeconds
                    : null),
            400 => new JiraValidationError(text.Length > 0 ? text : "Bad request (400)", statusCode),
            _ => new JiraApiError(text.Length > 0 ? text : $"Jira API error ({statusCode})", statusCode),
        };
    }

    /// <summary>
    /// Send a single authenticated request with rate limiting and retry. Returns a 2xx response,
    /// or throws a mapped <see cref="JiraError"/> subclass. On the first transient failure the
    /// call is retried with exponential backoff (honoring <c>Retry-After</c> for 429).
    /// </summary>
    public async Task<HttpResponseMessage> RequestAsync(
        HttpMethod method,
        string path,
        IDictionary<string, string?>? query = null,
        JsonNode? jsonBody = null,
        bool allow2xxOnly = true,
        int? maxRetries = null,
        CancellationToken cancellationToken = default)
    {
        var retries = maxRetries ?? MaxRetries;
        var attempt = 0;

        while (true)
        {
            await AcquireAsync(cancellationToken);

            using var request = new HttpRequestMessage(method, BuildUri(path, query));
            request.Headers.Authorization = CurrentAuthHeader();
            if (jsonBody is not null)
            {
                request.Content = JsonContent.Create(jsonBody, new MediaTypeHeaderValue("application/json"));
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < retries && retries > 0)
                {
                    attempt++;
                    var wait = RetriableBackoffSeconds[Math.Min(attempt - 1, RetriableBackoffSeconds.Length - 1)];
                    _logger.LogWarning("Jira request to {Method} {Path} timed out (attempt {Attempt}); retrying in {Wait}s",
                        method, path, attempt, wait);
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
                    continue;
                }

                throw new JiraApiError(
                    $"Request to {method} {path} timed out after {Settings.RequestTimeout}s",
                    statusCode: null);
            }

            // On success the caller owns the response (RequestJsonAsync disposes it); only the
            // retry and final-error paths dispose here. A `using` block would dispose on return,
            // handing the caller an already-disposed HttpResponseMessage whose decompressed content
            // (GZipDecompressedContent) then throws "Cannot access a disposed object".
            var status = (int)response.StatusCode;
            if (status < 400)
            {
                return response;
            }

            if (JiraErrorCatalog.IsTransientHttpStatus(status) && attempt < retries)
            {
                attempt++;
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter?.ToString());
                var wait = retryAfter is not null
                    ? retryAfter.Value - DateTimeOffset.UtcNow
                    : TimeSpan.FromSeconds(RetriableBackoffSeconds[Math.Min(attempt - 1, RetriableBackoffSeconds.Length - 1)]);
                if (wait < TimeSpan.Zero)
                {
                    wait = TimeSpan.Zero;
                }

                _logger.LogWarning("Jira returned {Status} for {Method} {Path}; retrying in {Wait}s",
                    status, method, path, wait.TotalSeconds);
                response.Dispose();
                await Task.Delay(wait, cancellationToken);
                continue;
            }

            var error = MapToJiraError(response, status);
            response.Dispose();
            throw error;
        }
    }

    private static Uri BuildUri(string path, IDictionary<string, string?>? query)
    {
        if (query is null || query.Count == 0)
        {
            return new Uri(path, UriKind.Relative);
        }

        var sb = new StringBuilder(path);
        var first = true;
        foreach (var (key, value) in query)
        {
            if (value is null)
            {
                continue;
            }

            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(Uri.EscapeDataString(key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
        }

        return new Uri(sb.ToString(), UriKind.Relative);
    }

    private async Task<JsonNode?> RequestJsonAsync(
        HttpMethod method,
        string path,
        IDictionary<string, string?>? query = null,
        JsonNode? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await RequestAsync(method, path, query, jsonBody, cancellationToken: cancellationToken);
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    // ------------------------------------------------------------------
    // Pagination
    // ------------------------------------------------------------------
    /// <summary>
    /// Iterate over a Jira list endpoint honoring <c>startAt</c>/<c>maxResults</c> pagination.
    /// Paths return <c>{"values": [...]}</c> (or <c>issues</c>/<c>comments</c>/etc.).
    /// </summary>
    public async IAsyncEnumerable<JsonObject> PageAsync(
        string path,
        IDictionary<string, string?>? query,
        int? limit,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startAt = 0;
        var maxResults = Math.Min(limit ?? JiraDefaults.PaginationLimit, JiraDefaults.PaginationLimit);
        var pageParams = new Dictionary<string, string?>(query ?? new Dictionary<string, string?>())
        {
            ["maxResults"] = maxResults.ToString(),
        };

        while (true)
        {
            pageParams["startAt"] = startAt.ToString();
            using var response = await RequestAsync(HttpMethod.Get, path, pageParams, cancellationToken: cancellationToken);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var values = ExtractValues(doc.RootElement);
            var total = doc.RootElement.TryGetProperty("total", out var totalElement) && totalElement.ValueKind == JsonValueKind.Number
                ? totalElement.GetInt64()
                : -1;

            var items = new List<JsonObject>();
            foreach (var value in values)
            {
                if (value is JsonObject obj)
                {
                    items.Add(obj);
                }
            }

            foreach (var item in items)
            {
                yield return item;
            }

            if (total < 0)
            {
                yield break; // not paginated
            }

            startAt += maxResults;
            if (startAt >= total || items.Count == 0)
            {
                yield break;
            }
        }
    }

    private static IEnumerable<JsonNode?> ExtractValues(JsonElement payload)
    {
        foreach (var key in new[] { "values", "issues", "comments", "transitions", "projects", "versions" })
        {
            if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray().Select(static e => (JsonNode?)JsonNode.Parse(e.GetRawText()));
            }
        }

        return Array.Empty<JsonNode?>();
    }

    // ------------------------------------------------------------------
    // Project whitelist enforcement
    // ------------------------------------------------------------------
    /// <summary>
    /// Inject the configured project whitelist into a JQL query. When <paramref name="projectKey"/>
    /// is supplied (for issue-scoped calls) no scope is injected because the caller already targets
    /// a specific project.
    /// </summary>
    public string WithProjectScope(string jql, string? projectKey = null)
    {
        var allowed = Settings.ConfiguredProjectKeys;
        if (allowed.Count == 0 || projectKey is not null)
        {
            return jql;
        }

        return Jql.Build(projectKeys: allowed, jqlParts: string.IsNullOrEmpty(jql) ? null : [jql]);
    }

    /// <summary>Reject writes to projects outside the whitelist (fast fail before any HTTP call).</summary>
    public void EnsureProjectAllowed(string projectKey)
    {
        var allowed = Settings.ConfiguredProjectKeys;
        if (allowed.Count > 0 && !allowed.Contains(Jql.NormalizeProjectKey(projectKey)))
        {
            throw new JiraPermissionError(
                $"Project {projectKey} is not in the configured allowlist " +
                $"[{string.Join(", ", allowed.OrderBy(static k => k))}]; refusing the operation",
                statusCode: 403);
        }
    }

    // ------------------------------------------------------------------
    // Issue operations
    // ------------------------------------------------------------------
    public async Task<JsonNode?> GetIssueAsync(string issueKey, IReadOnlyList<string>? fields = null, IReadOnlyList<string>? expand = null, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>();
        if (fields is { Count: > 0 })
        {
            query["fields"] = string.Join(",", fields);
        }

        if (expand is { Count: > 0 })
        {
            query["expand"] = string.Join(",", expand);
        }

        return await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/issue/{issueKey}", query, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> CreateIssueAsync(
        string projectKey,
        string summary,
        string issueType,
        JsonNode? descriptionAdf,
        string? priority = null,
        string? assigneeAccountId = null,
        IReadOnlyList<string>? labels = null,
        IReadOnlyList<string>? components = null,
        IDictionary<string, JsonNode?>? customFields = null,
        string? parentKey = null,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectAllowed(projectKey);
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["summary"] = summary,
            ["issuetype"] = new JsonObject { ["name"] = issueType },
        };

        if (descriptionAdf is not null)
        {
            fields["description"] = descriptionAdf;
        }

        if (!string.IsNullOrEmpty(priority))
        {
            fields["priority"] = new JsonObject { ["name"] = priority };
        }

        if (!string.IsNullOrEmpty(assigneeAccountId))
        {
            fields["assignee"] = new JsonObject { ["accountId"] = assigneeAccountId };
        }

        if (labels is { Count: > 0 })
        {
            fields["labels"] = new JsonArray(labels.Select(static l => (JsonNode?)l).ToArray());
        }

        if (components is { Count: > 0 })
        {
            fields["components"] = new JsonArray(components.Select(static c => (JsonNode?)new JsonObject { ["name"] = c }).ToArray());
        }

        if (customFields is { Count: > 0 })
        {
            foreach (var (key, value) in customFields)
            {
                fields[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(parentKey))
        {
            fields["parent"] = new JsonObject { ["key"] = parentKey };
        }

        return await RequestJsonAsync(HttpMethod.Post, "/rest/api/3/issue", jsonBody: fields, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> UpdateIssueAsync(string issueKey, IDictionary<string, JsonNode?>? fields, bool? notifyUsers, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        if (fields is { Count: > 0 })
        {
            body["fields"] = new JsonObject(fields.Select(static pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
        }

        var query = new Dictionary<string, string?>();
        if (notifyUsers is not null)
        {
            query["notifyUsers"] = notifyUsers.Value ? "true" : "false";
        }

        return await RequestJsonAsync(HttpMethod.Put, $"/rest/api/3/issue/{issueKey}", query,
            jsonBody: body, cancellationToken: cancellationToken);
    }

    public async Task DeleteIssueAsync(string issueKey, bool deleteSubtasks = false, CancellationToken cancellationToken = default)
    {
        await RequestAsync(HttpMethod.Delete, $"/rest/api/3/issue/{issueKey}",
            new Dictionary<string, string?> { ["deleteSubtasks"] = deleteSubtasks ? "true" : "false" },
            cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> GetTransitionsAsync(string issueKey, CancellationToken cancellationToken = default) =>
        await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/issue/{issueKey}/transitions", cancellationToken: cancellationToken);

    public async Task TransitionIssueAsync(string issueKey, string transitionId, IDictionary<string, JsonNode?>? fields = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["transition"] = new JsonObject { ["id"] = transitionId } };
        if (fields is { Count: > 0 })
        {
            body["fields"] = new JsonObject(fields.Select(static pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
        }

        await RequestAsync(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/transitions", jsonBody: body, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> AddCommentAsync(string issueKey, JsonNode bodyAdf, string? visibilityType = null, string? visibilityValue = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["body"] = bodyAdf };
        if (!string.IsNullOrEmpty(visibilityType) && !string.IsNullOrEmpty(visibilityValue))
        {
            body["visibility"] = new JsonObject { ["type"] = visibilityType, ["value"] = visibilityValue };
        }

        return await RequestJsonAsync(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/comment", jsonBody: body, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> GetCommentsAsync(string issueKey, int maxResults = 50, int startAt = 0, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["maxResults"] = Math.Min(maxResults, JiraDefaults.PaginationLimit).ToString(),
            ["startAt"] = startAt.ToString(),
        };

        return await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/issue/{issueKey}/comment", query, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> LinkIssuesAsync(string inwardIssueKey, string outwardIssueKey, string linkType, string? comment = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["type"] = new JsonObject { ["name"] = linkType },
            ["inwardIssue"] = new JsonObject { ["key"] = inwardIssueKey },
            ["outwardIssue"] = new JsonObject { ["key"] = outwardIssueKey },
        };

        if (!string.IsNullOrEmpty(comment))
        {
            body["comment"] = Formatters.Adf.TextToAdf(comment);
        }

        return await RequestJsonAsync(HttpMethod.Post, "/rest/api/3/issueLink", jsonBody: body, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> SearchIssuesAsync(
        string jql,
        int? maxResults = null,
        int startAt = 0,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<string>? expand = null,
        bool? fieldsByKeys = null,
        CancellationToken cancellationToken = default)
    {
        // The plain /rest/api/3/search was removed by Jira Cloud (410). The enhanced search is a
        // scrolling view: it does not accept startAt (paginates with nextPageToken), so startAt is
        // accepted for signature compatibility but ignored for the enhanced (POST) engine.
        var (method, path) = Settings.SearchApiPaths;
        var capped = Math.Min(maxResults ?? JiraDefaults.DefaultMaxResults, JiraDefaults.PaginationLimit);

        if (method == "GET")
        {
            var query = new Dictionary<string, string?>
            {
                ["jql"] = jql,
                ["maxResults"] = capped.ToString(),
            };

            if (fields is { Count: > 0 })
            {
                query["fields"] = string.Join(",", fields);
            }
            else if (expand is null)
            {
                query["fields"] = string.Join(",", JiraDefaults.DefaultSearchFields);
            }

            if (expand is { Count: > 0 })
            {
                query["expand"] = string.Join(",", expand);
            }

            if (fieldsByKeys is not null)
            {
                query["fieldsByKeys"] = fieldsByKeys.ToString();
            }

            return await RequestJsonAsync(HttpMethod.Get, path, query, cancellationToken: cancellationToken);
        }

        var body = new JsonObject { ["jql"] = jql, ["maxResults"] = capped };
        if (fields is { Count: > 0 })
        {
            body["fields"] = new JsonArray(fields.Select(static f => (JsonNode?)f).ToArray());
        }
        else if (expand is null)
        {
            body["fields"] = new JsonArray(JiraDefaults.DefaultSearchFields.Select(static f => (JsonNode?)f).ToArray());
        }

        if (expand is { Count: > 0 })
        {
            body["expand"] = new JsonArray(expand.Select(static e => (JsonNode?)e).ToArray());
        }

        if (fieldsByKeys is not null)
        {
            body["fieldsByKeys"] = fieldsByKeys;
        }

        return await RequestJsonAsync(HttpMethod.Post, path, jsonBody: body, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> GetCreateIssueMetaAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectKeys"] = projectKey,
            ["expand"] = "projects.issuetypes.fields",
        };

        return await RequestJsonAsync(HttpMethod.Get, "/rest/api/3/issue/createmeta", query, cancellationToken: cancellationToken);
    }

    // ------------------------------------------------------------------
    // Project operations
    // ------------------------------------------------------------------
    public async Task<JsonNode?> ListProjectsAsync(int? maxResults = null, CancellationToken cancellationToken = default)
    {
        return await RequestJsonAsync(
            HttpMethod.Get, "/rest/api/3/project",
            new Dictionary<string, string?> { ["maxResults"] = Math.Min(maxResults ?? 50, JiraDefaults.PaginationLimit).ToString() },
            cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> GetProjectAsync(string projectKey, CancellationToken cancellationToken = default) =>
        await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/project/{projectKey}", cancellationToken: cancellationToken);

    public async Task<JsonNode?> GetProjectVersionsAsync(string projectKey, CancellationToken cancellationToken = default) =>
        await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/project/{projectKey}/versions", cancellationToken: cancellationToken);

    // ------------------------------------------------------------------
    // User operations
    // ------------------------------------------------------------------
    public async Task<JsonNode?> SearchUsersAsync(string query, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        return await RequestJsonAsync(
            HttpMethod.Get, "/rest/api/3/user/search",
            new Dictionary<string, string?>
            {
                ["query"] = query,
                ["maxResults"] = Math.Min(maxResults, JiraDefaults.PaginationLimit).ToString(),
            },
            cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> GetMyselfAsync(CancellationToken cancellationToken = default) =>
        await RequestJsonAsync(HttpMethod.Get, "/rest/api/3/myself", cancellationToken: cancellationToken);

    // ------------------------------------------------------------------
    // Attachment operations
    // ------------------------------------------------------------------
    public async Task<JsonNode?> GetAttachmentAsync(string attachmentId, CancellationToken cancellationToken = default) =>
        await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/attachment/{attachmentId}", cancellationToken: cancellationToken);

    public async Task<JsonNode?> GetAttachmentMetadataAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        var issue = await GetIssueAsync(issueKey, fields: ["attachment"], cancellationToken: cancellationToken);
        if (issue is JsonObject obj && obj["fields"] is JsonObject fields && fields["attachment"] is JsonArray attachments)
        {
            return attachments;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Worklog operations
    // ------------------------------------------------------------------
    public async Task<JsonNode?> GetWorklogsAsync(string issueKey, CancellationToken cancellationToken = default) =>
        await RequestJsonAsync(HttpMethod.Get, $"/rest/api/3/issue/{issueKey}/worklog", cancellationToken: cancellationToken);

    public async Task<JsonNode?> AddWorklogAsync(string issueKey, string timeSpent, string? comment = null, string? started = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["timeSpent"] = timeSpent };
        if (!string.IsNullOrEmpty(comment))
        {
            body["comment"] = Formatters.Adf.TextToAdf(comment);
        }

        if (!string.IsNullOrEmpty(started))
        {
            body["started"] = started;
        }

        return await RequestJsonAsync(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/worklog", jsonBody: body, cancellationToken: cancellationToken);
    }

    // ------------------------------------------------------------------
    // Agile (board/sprint) operations
    // ------------------------------------------------------------------
    public async Task<JsonNode?> GetBoardIssuesAsync(int boardId, int? maxResults = null, string? jql = null, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["maxResults"] = Math.Min(maxResults ?? JiraDefaults.DefaultMaxResults, JiraDefaults.PaginationLimit).ToString(),
        };

        if (!string.IsNullOrEmpty(jql))
        {
            query["jql"] = jql;
        }

        return await RequestJsonAsync(HttpMethod.Get, $"/rest/agile/1.0/board/{boardId}/issue", query, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> GetSprintIssuesAsync(int sprintId, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        return await RequestJsonAsync(
            HttpMethod.Get, $"/rest/agile/1.0/sprint/{sprintId}/issue",
            new Dictionary<string, string?> { ["maxResults"] = Math.Min(maxResults ?? JiraDefaults.DefaultMaxResults, JiraDefaults.PaginationLimit).ToString() },
            cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> ListSprintsAsync(int boardId, string? state = null, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["maxResults"] = Math.Min(maxResults ?? 50, JiraDefaults.PaginationLimit).ToString(),
        };

        if (!string.IsNullOrEmpty(state))
        {
            query["state"] = state;
        }

        return await RequestJsonAsync(HttpMethod.Get, $"/rest/agile/1.0/board/{boardId}/sprint", query, cancellationToken: cancellationToken);
    }

    public async Task<JsonNode?> ListBoardsAsync(string? projectKey = null, string? boardType = null, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?> { ["maxResults"] = "50" };
        if (!string.IsNullOrEmpty(projectKey))
        {
            query["projectKeyOrID"] = projectKey;
        }

        if (!string.IsNullOrEmpty(boardType))
        {
            query["type"] = boardType;
        }

        return await RequestJsonAsync(HttpMethod.Get, "/rest/agile/1.0/board", query, cancellationToken: cancellationToken);
    }

    public async Task MoveIssuesToSprintAsync(int sprintId, IReadOnlyList<string> issueKeys, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["issues"] = new JsonArray(issueKeys.Select(static k => (JsonNode?)k).ToArray()) };
        await RequestAsync(HttpMethod.Post, $"/rest/agile/1.0/sprint/{sprintId}/issue", jsonBody: body, cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rateLimiter.Dispose();
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// Send a fully-formed HTTP request (e.g. a multipart upload, where the JSON helper cannot be
    /// used) through this client's rate limiter and retry pipeline. The <paramref name="request"/>
    /// will have its Authorization header set against the live settings; any login the caller set is
    /// overridden, so the credential always comes from the server-side configuration.
    /// </summary>
    public async Task<HttpResponseMessage> RequestRawAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        var retries = MaxRetries;
        var relative = request.RequestUri is { } uri
            ? (uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString)
            : request.RequestUri?.ToString() ?? "?";

        while (true)
        {
            await AcquireAsync(cancellationToken);
            request.Headers.Authorization = CurrentAuthHeader();

            try
            {
                // Deep-copy is not possible for a reusable request; instead we send the caller's
                // request and dispose only the response (the caller owns the request content).
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                if (JiraErrorCatalog.IsTransientHttpStatus(status) && attempt < retries)
                {
                    attempt++;
                    var retryAfter = ParseRetryAfter(response.Headers.RetryAfter?.ToString());
                    var wait = retryAfter is not null
                        ? retryAfter.Value - DateTimeOffset.UtcNow
                        : TimeSpan.FromSeconds(RetriableBackoffSeconds[Math.Min(attempt - 1, RetriableBackoffSeconds.Length - 1)]);
                    if (wait < TimeSpan.Zero)
                    {
                        wait = TimeSpan.Zero;
                    }

                    response.Dispose();
                    _logger.LogWarning("Jira returned {Status} for {Path}; retrying in {Wait}s",
                        status, relative, wait.TotalSeconds);
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < retries && retries > 0)
                {
                    attempt++;
                    var wait = TimeSpan.FromSeconds(RetriableBackoffSeconds[Math.Min(attempt - 1, RetriableBackoffSeconds.Length - 1)]);
                    _logger.LogWarning("Jira request to {Path} timed out (attempt {Attempt}); retrying in {Wait}s",
                        relative, attempt, wait.TotalSeconds);
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                throw new JiraApiError($"Request to {relative} timed out after {Settings.RequestTimeout}s");
            }
        }
    }
}
