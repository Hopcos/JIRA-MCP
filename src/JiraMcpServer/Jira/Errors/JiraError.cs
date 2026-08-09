using System.Collections.ObjectModel;

namespace JiraMcpServer.Jira.Errors;

/// <summary>
/// Unified exception hierarchy for Jira API interactions.
/// Carries the HTTP status code, human-readable messages, optional warnings,
/// and structured detail. Subclasses pick out the status codes that need
/// special treatment (retry, or a reauthorization hint) without callers
/// having to switch on magic numbers.
/// </summary>
public class JiraError : Exception
{
    public JiraError(
        string message,
        int? statusCode = null,
        IEnumerable<string>? messages = null,
        IEnumerable<string>? warnings = null,
        IDictionary<string, object?>? detail = null)
        : base(message)
    {
        StatusCode = statusCode;
        Messages = new ReadOnlyCollection<string>(
            messages is null
                ? (string.IsNullOrEmpty(message) ? [] : [message])
                : new List<string>(messages));
        Warnings = new ReadOnlyCollection<string>(warnings?.ToList() ?? []);
        Detail = detail is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(detail);
    }

    public int? StatusCode { get; }

    public IReadOnlyList<string> Messages { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IDictionary<string, object?> Detail { get; }

    public override string ToString()
    {
        var joined = string.Join(" ", Messages);
        return StatusCode is int status
            ? $"Jira API Error [{status}]: {joined}"
            : $"Jira Error: {joined}";
    }
}

/// <summary>Generic error returned by the Jira REST API (non-2xx).</summary>
public sealed class JiraApiError : JiraError
{
    public JiraApiError(string message, int? statusCode = null, IEnumerable<string>? messages = null, IEnumerable<string>? warnings = null, IDictionary<string, object?>? detail = null)
        : base(message, statusCode, messages, warnings, detail)
    {
    }
}

/// <summary>401: the API token is invalid, expired, or tied to different permissions.</summary>
public sealed class JiraAuthenticationError : JiraError
{
    public JiraAuthenticationError(string message, int? statusCode = null, IEnumerable<string>? messages = null, IEnumerable<string>? warnings = null, IDictionary<string, object?>? detail = null)
        : base(message, statusCode, messages, warnings, detail)
    {
    }
}

/// <summary>403: the token is valid but lacks permission for this operation.</summary>
public sealed class JiraPermissionError : JiraError
{
    public JiraPermissionError(string message, int? statusCode = null, IEnumerable<string>? messages = null, IEnumerable<string>? warnings = null, IDictionary<string, object?>? detail = null)
        : base(message, statusCode, messages, warnings, detail)
    {
    }
}

/// <summary>404: the requested issue/project/resource does not exist.</summary>
public sealed class JiraNotFoundError : JiraError
{
    public JiraNotFoundError(string message, int? statusCode = null, IEnumerable<string>? messages = null, IEnumerable<string>? warnings = null, IDictionary<string, object?>? detail = null)
        : base(message, statusCode, messages, warnings, detail)
    {
    }
}

/// <summary>400: the request body was rejected by Jira's field validation.</summary>
public sealed class JiraValidationError : JiraError
{
    public JiraValidationError(string message, int? statusCode = null, IEnumerable<string>? messages = null, IEnumerable<string>? warnings = null, IDictionary<string, object?>? detail = null)
        : base(message, statusCode, messages, warnings, detail)
    {
    }
}

/// <summary>429: the remote rate limit was hit (server-side quota, not client-side).</summary>
public sealed class JiraRateLimitError : JiraError
{
    public JiraRateLimitError(
        string message,
        int? statusCode = 429,
        IEnumerable<string>? messages = null,
        IEnumerable<string>? warnings = null,
        double? retryAfter = null,
        IDictionary<string, object?>? detail = null)
        : base(message, statusCode, messages, warnings, detail)
    {
        RetryAfter = retryAfter;
        if (retryAfter is not null)
        {
            Detail["retry_after"] = retryAfter;
        }
    }

    public double? RetryAfter { get; }
}

public static class JiraErrorCatalog
{
    /// <summary>True for statuses that justify a retry with backoff (proxy/sporadic).</summary>
    public static bool IsTransientHttpStatus(int statusCode) =>
        statusCode is 429 or 500 or 502 or 503 or 504;

    /// <summary>Sentinel used by callers that must not leak raw API tokens into error text.</summary>
    public const string Redacted = "[redacted by jira-mcp-server]";
}
