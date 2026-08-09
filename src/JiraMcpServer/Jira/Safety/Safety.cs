using System.Text.Json;

namespace JiraMcpServer.Jira.Safety;

/// <summary>
/// Value helpers that enforce the server's credential-handling security contract:
/// <list type="bullet">
///   <item>credentials are only ever read from server-side configuration, never the wire;</item>
///   <item>logs and error output use <see cref="MaskToken"/> so a real token never reaches an external sink verbatim;</item>
///   <item>any credential value is normalized (whitespace trimmed, empty collapsed) so a half-configured
///   environment fails fast instead of authenticating with garbage.</item>
/// </list>
/// </summary>
public static class Safety
{
    public const int VisibleTokenPrefix = 5;
    public const string TokenMarker = "****";

    /// <summary>Return a masked preview of a token suitable for logging: <c>ATATT****</c>.</summary>
    public static string MaskToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "<not set>";
        }

        if (token.Length <= VisibleTokenPrefix + TokenMarker.Length)
        {
            return TokenMarker;
        }

        return token[..VisibleTokenPrefix] + TokenMarker;
    }

    /// <summary>
    /// Recursively mask every token-like string inside an arbitrary value.
    /// The header <c>Authorization</c> bearing a Basic/Bearer credential and free-form comment
    /// bodies occasionally contain a token. Matching is deliberately conservative (preview plus
    /// at least six base64-ish characters), so ordinary short words are never mangled.
    /// </summary>
    public static object? MaskValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => MaskTokenLikeString(s),
            bool or byte or short or int or long or float or double or decimal => value,
            System.Collections.IDictionary map => MaskDictionary(map),
            IEnumerable<object?> seq => seq.Select(MaskValue).ToList(),
            IDictionary<string, object?> dict => dict.ToDictionary(
                static pair => pair.Key,
                static pair => MaskValue(pair.Value)),
            JsonElement element => MaskJsonElement(element),
            _ => value,
        };
    }

    private static string MaskTokenLikeString(string value)
    {
        if (value.Length == 0 || value.Length < VisibleTokenPrefix + 6)
        {
            return value;
        }

        // \bATTT3x6xxxxxx\b style tokens: optional A prefix, at least six
        // base64-ish characters. Bash-style alternation is not available in
        // .NET regex without backtracking; model it as a char class.
        return System.Text.RegularExpressions.Regex.Replace(
            value,
            @"\b[A]{0,1}[A-Za-z0-9_-]{6,}\b",
            m => m.Value.Length >= 8 ? MaskToken(m.Value) : m.Value);
    }

    private static object MaskJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => MaskTokenLikeString(element.GetString() ?? ""),
            JsonValueKind.Array => element.EnumerateArray().Select(MaskJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(static p => p.Name, static p => MaskJsonElement(p.Value)),
            _ => JsonJson(element),
        };

        static object JsonJson(JsonElement el) =>
            el.ValueKind switch
            {
                JsonValueKind.Null => null!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when el.TryGetInt64(out var i) => i,
                JsonValueKind.Number => el.GetDouble(),
                _ => el.ToString(),
            };
    }

    private static System.Collections.IDictionary MaskDictionary(System.Collections.IDictionary map)
    {
        var result = new System.Collections.Generic.Dictionary<string, object?>();
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            result[entry.Key?.ToString() ?? ""] = MaskValue(entry.Value);
        }

        return result;
    }

    /// <summary>
    /// Collapse empty/whitespace credential values to <c>null</c>. Environment files frequently
    /// contain <c>JIRA_API_TOKEN=</c> (empty) or a stray newline; both behave like an unset
    /// variable and fail configuration validation with a clear message.
    /// </summary>
    public static string? NormalizeCredential(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

/// <summary>
/// Defensive filter guaranteeing the model never sees values named
/// <c>token</c>/<c>password</c>/<c>credentials</c>/etc., no matter what Jira (or a proxy)
/// returns. Nested values are masked token-like strings too.
/// </summary>
public static class SearchResultSanitizer
{
    private static readonly System.Collections.Generic.HashSet<string> CredentialKeys =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            "token",
            "access_token",
            "refresh_token",
            "credentials",
            "authorization",
            "api_token",
            "password",
            "secret",
            "errorMessages",
            "errors",
        };

    public static Dictionary<string, object?> Sanitize(JsonElement payload)
    {
        var cleaned = new Dictionary<string, object?>();
        foreach (var property in payload.EnumerateObject())
        {
            if (CredentialKeys.Contains(property.Name))
            {
                continue;
            }

            cleaned[property.Name] = Safety.MaskValue(property.Value);
        }

        return cleaned;
    }

    /// <summary><see cref="Sanitize(System.Text.Json.JsonElement)"/> for a <see cref="System.Text.Json.Nodes.JsonObject"/>.</summary>
    public static System.Text.Json.Nodes.JsonObject Sanitize(System.Text.Json.Nodes.JsonObject payload)
    {
        var cleaned = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in payload)
        {
            if (CredentialKeys.Contains(name))
            {
                continue;
            }

            cleaned[name] = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(Safety.MaskValue(value)))?.DeepClone();
        }

        return cleaned;
    }
}
