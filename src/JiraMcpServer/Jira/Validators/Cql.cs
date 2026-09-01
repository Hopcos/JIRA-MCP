using System.Globalization;

namespace JiraMcpServer.Jira.Validators;

/// <summary>
/// Input validation: safe Confluence Query Language (CQL) construction, mirroring the JQL builder
/// (<see cref="Jql"/>). This is the only place that composes a CQL string; every literal is
/// escaped with <see cref="EscapeValue"/> so crafted input containing quotes or operators cannot
/// alter the query. The convenience search filters (<c>query</c>/<c>space_key</c>/<c>title</c>/<c>type</c>)
/// are combined through <see cref="Build"/> into one <c>AND</c>-joined query.
/// </summary>
public static class Cql
{
    /// <summary>
    /// Escape and quote a value for safe interpolation into CQL. Strings become double-quoted with
    /// embedded quotes/backslashes escaped; non-string values map to Confluence literals.
    /// </summary>
    public static string EscapeValue(object? value)
    {
        return value switch
        {
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            bool b => b ? "true" : "false",
            short or ushort or int or uint or long or ulong or float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "EMPTY",
            System.Collections.IEnumerable seq when value is not string =>
                "(" + string.Join(", ", seq.Cast<object?>().Select(EscapeValue)) + ")",
            null => "EMPTY",
            _ => EscapeValue(value.ToString()),
        };
    }

    /// <summary>
    /// Combine CQL fragments into one query with <c>AND</c>. Empty/whitespace fragments are
    /// dropped; a single fragment is returned verbatim; a zero-fragment set returns <c>""</c>.
    /// </summary>
    public static string Build(IReadOnlyList<string>? parts = null)
    {
        var clauses = (parts ?? Array.Empty<string>())
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        return clauses.Count switch
        {
            0 => "",
            1 => clauses[0],
            _ => string.Join(" AND ", clauses.Select(static c => $"({c})")),
        };
    }
}
