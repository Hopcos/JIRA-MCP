using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Jira.Safety;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Tools;

/// <summary>
/// Immutable per-request context shared by every MCP tool: the Jira client and the compiled
/// server configuration. Tools receive this through constructor injection (the SDK constructs a
/// new tool instance per invocation), so each tool can resolve the services it needs with the
/// current request's configuration.
/// </summary>
public sealed class JiraToolContext
{
    public required JiraClient Client { get; init; }

    public required CompiledConfig Settings { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>
    /// Annotate a search payload with the effective project scope. When a project whitelist is
    /// configured, JQL is injected with a <c>project in (...)/project = "..."</c> clause the model
    /// may not be aware of; attaching the scope lets the model understand why a search returns
    /// nothing for a project outside the allowlist.
    /// </summary>
    public void AttachScopeHint(System.Text.Json.Nodes.JsonObject payload)
    {
        IReadOnlyList<string> allowed;
        try
        {
            allowed = Settings.ConfiguredProjectKeys;
        }
        catch (Exception)
        {
            allowed = Array.Empty<string>();
        }

        if (allowed.Count == 0)
        {
            return;
        }

        payload["scope"] = new System.Text.Json.Nodes.JsonObject
        {
            ["allowed_projects"] = new System.Text.Json.Nodes.JsonArray(
                allowed.OrderBy(static k => k).Select(static k => (System.Text.Json.Nodes.JsonNode?)k).ToArray()),
        };

        if (payload["issues"] is not System.Text.Json.Nodes.JsonArray issues || issues.Count == 0)
        {
            payload["note"] =
                $"No issues matched within the configured project allowlist " +
                $"[{string.Join(", ", allowed.OrderBy(static k => k))}]; projects outside it are excluded from search.";
        }
    }
}

/// <summary>
/// Shared static helpers for building tool result payloads, used by every tool module.
/// </summary>
public static class ToolPayload
{
    /// <summary>Summarize a project list to reduce context bloat: key/name/type/lead.</summary>
    public static System.Text.Json.Nodes.JsonArray SummarizeProjects(System.Text.Json.Nodes.JsonNode? projects)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (projects is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                result.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["key"] = GetString(item, "key"),
                    ["name"] = GetString(item, "name"),
                    ["type"] = GetString(item, "projectTypeKey"),
                    ["lead"] = GetNestedString(item, "lead", "displayName"),
                });
            }
        }

        return result;
    }

    /// <summary>Summarize a board list: id/name/type/location.</summary>
    public static System.Text.Json.Nodes.JsonArray SummarizeBoards(System.Text.Json.Nodes.JsonNode? boards)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (boards is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                result.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = GetValue(item, "id"),
                    ["name"] = GetString(item, "name"),
                    ["type"] = GetString(item, "type"),
                    ["location"] = GetNestedString(item, "location", "projectKey"),
                });
            }
        }

        return result;
    }

    /// <summary>Summarize sprint issues: key/summary/status/assignee.</summary>
    public static System.Text.Json.Nodes.JsonArray SummarizeIssues(System.Text.Json.Nodes.JsonNode? issues)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (issues is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                var fields = item["fields"] as System.Text.Json.Nodes.JsonObject;
                result.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["key"] = GetString(item, "key"),
                    ["summary"] = fields is null ? null : GetString(fields, "summary"),
                    ["status"] = fields is null ? null : GetNestedString(fields, "status", "name"),
                    ["assignee"] = fields is null ? null : GetNestedString(fields, "assignee", "displayName"),
                });
            }
        }

        return result;
    }

    /// <summary>Privacy-filtered user list: accountId/displayName/active.</summary>
    public static System.Text.Json.Nodes.JsonArray SanitizeUsers(System.Text.Json.Nodes.JsonNode? users)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (users is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                result.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["accountId"] = GetString(item, "accountId"),
                    ["displayName"] = GetString(item, "displayName"),
                    ["active"] = GetValue(item, "active"),
                });
            }
        }

        return result;
    }

    /// <summary>Summarize attachments: id/filename/size/mimeType/author/created.</summary>
    public static System.Text.Json.Nodes.JsonArray SummarizeAttachments(System.Text.Json.Nodes.JsonNode? attachments)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (attachments is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                result.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = GetString(item, "id"),
                    ["filename"] = GetString(item, "filename"),
                    ["size"] = GetValue(item, "size"),
                    ["mimeType"] = GetString(item, "mimeType"),
                    ["author"] = GetNestedString(item, "author", "displayName"),
                    ["created"] = GetString(item, "created"),
                });
            }
        }

        return result;
    }

    /// <summary>Summarize worklogs: id/author/timeSpentSeconds/started/comment.</summary>
    public static System.Text.Json.Nodes.JsonArray SummarizeWorklogs(System.Text.Json.Nodes.JsonNode? worklogs)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (worklogs is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                result.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = GetString(item, "id"),
                    ["author"] = GetNestedString(item, "author", "displayName"),
                    ["timeSpentSeconds"] = GetValue(item, "timeSpentSeconds"),
                    ["started"] = GetString(item, "started"),
                    ["comment"] = GetValue(item, "comment"),
                });
            }
        }

        return result;
    }

    public static string? GetString(System.Text.Json.Nodes.JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        if (node is System.Text.Json.Nodes.JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToString();
    }

    public static System.Text.Json.Nodes.JsonNode? GetValue(System.Text.Json.Nodes.JsonObject obj, string key)
    {
        var node = obj[key];
        if (node is null)
        {
            return null;
        }

        // Preserve the raw JSON structure the way the Python original passed dict values through.
        return node.DeepClone();
    }

    public static string? GetNestedString(System.Text.Json.Nodes.JsonObject obj, string outer, string inner)
    {
        if (obj[outer] is System.Text.Json.Nodes.JsonObject nested)
        {
            return GetString(nested, inner);
        }

        return null;
    }

    /// <summary>Extract a compact issue summary list from a search payload (key/summary/status/assignee).</summary>
    public static System.Text.Json.Nodes.JsonArray ExtractBrevity(System.Text.Json.Nodes.JsonNode? payload)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        if (payload is not System.Text.Json.Nodes.JsonObject root ||
            root["issues"] is not System.Text.Json.Nodes.JsonArray issues)
        {
            return result;
        }

        foreach (var item in issues.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var fields = item["fields"] as System.Text.Json.Nodes.JsonObject;
            result.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = GetString(item, "key"),
                ["summary"] = fields is null ? null : GetString(fields, "summary"),
                ["status"] = fields is null ? null : GetNestedString(fields, "status", "name"),
                ["assignee"] = fields is null ? null : GetNestedString(fields, "assignee", "displayName"),
            });
        }

        return result;
    }
}
