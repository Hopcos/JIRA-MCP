namespace JiraMcpServer.Tools.Permissions;

/// <summary>
/// Tool-level permission control (CRUD-style enable/disable).
/// Maps every registered MCP tool to an operation category so operators can limit
/// which tools a Jira token may drive. Selection comes from <c>JIRA_TOOLS</c>
/// (comma-separated). An empty/unset value means all tools are enabled.
/// </summary>
public static class Permissions
{
    public static readonly IReadOnlySet<string> ReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jira_get_issue",
        "jira_get_transitions",
        "jira_get_comments",
        "jira_get_issue_links",
        "jira_get_issue_meta",
        "jira_get_project_meta",
        "jira_search_issues",
        "jira_search_issues_jql_only",
        "jira_list_projects",
        "jira_get_project",
        "jira_get_project_versions",
        "jira_list_boards",
        "jira_list_sprints",
        "jira_get_sprint_issues",
        "jira_list_attachments",
        "jira_get_worklogs",
        "jira_search_users",
        "jira_get_myself",
        "confluence_get_page",
        "confluence_search",
        "confluence_list_spaces",
        "confluence_get_space",
    };

    public static readonly IReadOnlySet<string> CreateTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jira_create_issue",
        "jira_add_comment",
        "jira_add_attachment",
        "jira_add_worklog",
        "jira_link_issues",
        "jira_move_issues_to_sprint",
    };

    public static readonly IReadOnlySet<string> UpdateTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jira_update_issue",
        "jira_transition_issue",
    };

    public static readonly IReadOnlySet<string> DeleteTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jira_delete_issue",
    };

    public static readonly IReadOnlySet<string> WriteTools = CreateTools.Union(UpdateTools).Union(DeleteTools)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> AllTools = ReadTools.Union(WriteTools)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Categories =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["read"] = ReadTools,
            ["create"] = CreateTools,
            ["update"] = UpdateTools,
            ["delete"] = DeleteTools,
            ["write"] = WriteTools,
        };

    /// <summary>
    /// Resolve a <c>JIRA_TOOLS</c> string into an explicit set of tool names.
    /// Returns a non-empty set (the allowlist), or throws for unknown keywords/tool names
    /// so a typo fails at configuration time instead of silently dropping tools.
    /// </summary>
    public static HashSet<string> ParseTools(string? configured)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return allowed;
        }

        foreach (var raw in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = raw.Trim().ToLowerInvariant();
            if (Categories.TryGetValue(item, out var expansion))
            {
                allowed.UnionWith(expansion);
            }
            else if (AllTools.Contains(item))
            {
                allowed.Add(item);
            }
            else
            {
                throw new ArgumentException(
                    $"Invalid JIRA_TOOLS entry '{item}': expected a tool name (e.g. jira_get_issue) " +
                    $"or one of [{string.Join(", ", Categories.Keys)}]");
            }
        }

        return allowed;
    }

    /// <summary>
    /// Build an allowlist predicate. An empty allowlist set means "all tools enabled".
    /// </summary>
    public static Func<string, bool> IsToolEnabled(HashSet<string>? allowed) =>
        allowed is null || allowed.Count == 0
            ? static _ => true
            : name => allowed.Contains(name);
}
