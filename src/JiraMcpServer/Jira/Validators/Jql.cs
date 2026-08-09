using System.Text;

namespace JiraMcpServer.Jira.Validators;

/// <summary>
/// Input validation: safe JQL construction (injection-resistant) and project-key normalization.
/// The JQL builder is the only place that composes a JQL string; it encloses every literal
/// programmatically with proper trait quoting so crafted input containing quotes or operators
/// like <c>OR</c> cannot alter the query. The project whitelist is injected here (see
/// <c>JiraClient.WithProjectScope</c>), and this is the line the security review calls out.
/// </summary>
public static class Jql
{
    /// <summary>
    /// Combine project scoping and additional JQL snippets into one query.
    /// <paramref name="jqlParts"/> are trusted config/system strings; free-form JQL from a tool
    /// argument is forwarded verbatim, so tool writers must use <see cref="EscapeValue"/> for any
    /// interpolated literal. A trailing <c>ORDER BY</c> is hoisted to the end of the assembled query.
    /// </summary>
    public static string Build(
        IReadOnlyList<string>? projectKeys = null,
        IReadOnlyList<string>? issueKeys = null,
        IReadOnlyList<string>? jqlParts = null)
    {
        var scopes = new List<string>();
        if (projectKeys is { Count: > 0 })
        {
            scopes.Add(projectKeys.Count == 1
                ? $"project = \"{projectKeys[0]}\""
                : "project in (" + string.Join(", ", projectKeys) + ")");
        }

        if (issueKeys is { Count: > 0 })
        {
            scopes.Add(issueKeys.Count == 1
                ? $"issuekey = \"{issueKeys[0]}\""
                : "issuekey in (" + string.Join(", ", issueKeys) + ")");
        }

        var orderByParts = new List<string>();
        var cleanedParts = new List<string>();
        foreach (var clause in jqlParts ?? Array.Empty<string>())
        {
            var (baseClause, orderBy) = SplitOrderBy(clause);
            if (!string.IsNullOrWhiteSpace(baseClause))
            {
                cleanedParts.Add(baseClause);
            }

            if (!string.IsNullOrWhiteSpace(orderBy))
            {
                orderByParts.Add(orderBy);
            }
        }

        var clauses = scopes.Concat(cleanedParts).Where(static c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (clauses.Count == 0 && orderByParts.Count == 0)
        {
            return "";
        }

        var query = clauses.Count switch
        {
            0 => "",
            1 => clauses[0],
            _ => string.Join(" AND ", clauses.Select(static c => $"({c})")),
        };

        if (orderByParts.Count > 0)
        {
            if (!string.IsNullOrEmpty(query))
            {
                query += " ";
            }

            query += "ORDER BY " + string.Join(", ", orderByParts);
        }

        return query;
    }

    /// <summary>
    /// Escape and quote a value for safe interpolation into JQL. Strings become double-quoted with
    /// embedded quotes/backslashes escaped; non-string values map to Jira literals, preventing type
    /// confusion and injection via a crafted string.
    /// </summary>
    public static string EscapeValue(object? value)
    {
        return value switch
        {
            string s when s.Trim().ToUpperInvariant() == "EMPTY" => "EMPTY",
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            bool b => b ? "true" : "false",
            short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "EMPTY",
            System.Collections.IEnumerable seq when value is not string => "(" +
                string.Join(", ", seq.Cast<object?>().Select(EscapeValue)) + ")",
            null => "EMPTY",
            _ => EscapeValue(value.ToString()),
        };
    }

    /// <summary>
    /// Split a top-level trailing <c>ORDER BY</c> out of a JQL clause. Only splits when it appears
    /// at top level (not inside parentheses/brackets); quoted strings are honored so a value
    /// containing <c>ORDER BY</c> is not mis-split.
    /// </summary>
    internal static (string? BaseClause, string? OrderBy) SplitOrderBy(string clause)
    {
        var depth = 0;
        char? inQuote = null;
        var escaped = false;

        for (var i = 0; i < clause.Length; i++)
        {
            var ch = clause[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inQuote is not null)
            {
                if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == inQuote)
                {
                    inQuote = null;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                inQuote = ch;
            }
            else if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 &&
                     clause.AsSpan(i).StartsWith("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
                     (i + 8 >= clause.Length || !char.IsLetterOrDigit(clause[i + 8])))
            {
                var before = clause[..i];
                var rest = clause[(i + 8)..].TrimStart();
                if (string.IsNullOrWhiteSpace(before))
                {
                    return (null, rest);
                }

                return (before.TrimEnd(), rest);
            }
        }

        return (clause, null);
    }

    /// <summary>Uppercase and strip a project key for whitelist comparisons.</summary>
    public static string NormalizeProjectKey(string key) => key.Trim().ToUpperInvariant();

    /// <summary>Reject empty strings supplied where a project/board key is required.</summary>
    public static string EnsureNonEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty");
        }

        return value.Trim();
    }
}
