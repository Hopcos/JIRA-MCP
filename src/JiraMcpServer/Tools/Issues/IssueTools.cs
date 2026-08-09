using System.ComponentModel;
using System.Text.Json.Nodes;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Jira.Formatters;
using JiraMcpServer.Jira.Safety;
using JiraMcpServer.Jira.Validators;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace JiraMcpServer.Tools.Issues;

/// <summary>
/// Issue-related MCP tools: create/update/get/delete/transition/comment/link/search.
/// Project whitelist enforcement is automatic through <c>JiraClient.WithProjectScope</c>, so a
/// deployment scoped to specific projects can never leak issues from other projects.
/// Each handler returns a <see cref="CallToolResult"/> built by <see cref="ToolResult"/>.
/// </summary>
[McpServerToolType]
public sealed class IssueTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerTool(Name = "jira_create_issue", Title = "Create a Jira issue")]
    [Description("Create a Jira issue in a project. Automatically converts a plain-text description into Atlassian Document Format. Optionally honors the configured project allowlist.")]
    public async Task<CallToolResult> CreateIssueAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        [Description("Issue summary/title")] string summary,
        [Description("Issue type name, e.g. Bug, Story, Task, Epic, Sub-task")] string issueType,
        [Description("Plain-text or ADF JSON description")] string? description = null,
        [Description("Priority name, e.g. High")] string? priority = null,
        [Description("Atlassian account ID of the assignee")] string? assigneeAccountId = null,
        [Description("Issue labels")] IReadOnlyList<string>? labels = null,
        [Description("Component names")] IReadOnlyList<string>? components = null,
        [Description("Custom field IDs to values")] System.Text.Json.Nodes.JsonObject? customFields = null,
        [Description("Parent issue key when creating a sub-task")] string? parentKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _ctx.Client.CreateIssueAsync(
                Jql.NormalizeProjectKey(projectKey),
                summary,
                issueType,
                description is null ? null : Adf.TextToAdf(description),
                priority,
                assigneeAccountId,
                labels,
                components,
                CustomFieldsToDict(customFields),
                parentKey,
                cancellationToken);
            return ToolResult.DictResult(result, "issue");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_update_issue", Title = "Update a Jira issue")]
    [Description("Update one or more fields of an existing issue.")]
    public async Task<CallToolResult> UpdateIssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Map of field IDs (or names) to values, e.g. {'summary': 'New title'}")] System.Text.Json.Nodes.JsonObject fields,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _ctx.Client.UpdateIssueAsync(issueKey, FieldsToDict(fields), notifyUsers: null, cancellationToken);
            return ToolResult.DictResult(new { issue_key = issueKey, updated = true }, "result");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_issue", Title = "Get a Jira issue")]
    [Description("Return the full details of a single issue, optionally filtered by fields and expand.")]
    public async Task<CallToolResult> GetIssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Field names (or ids) to return; default all")] IReadOnlyList<string>? fields = null,
        [Description("Optional expand values like changelog or renderedFields")] IReadOnlyList<string>? expand = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _ctx.Client.GetIssueAsync(issueKey, fields, expand, cancellationToken);
            return ToolResult.DictResult(data, "issue");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_delete_issue", Title = "Delete a Jira issue")]
    [Description("Permanently delete an issue. Destructive and irreversible; requires confirm=true to proceed. With confirm=false, returns a preview and a warning instead of deleting.")]
    public async Task<CallToolResult> DeleteIssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Must be true to actually delete; false returns a preview")] bool confirm,
        [Description("Also delete subtasks")] bool deleteSubtasks = false,
        CancellationToken cancellationToken = default)
    {
        if (confirm)
        {
            try
            {
                await _ctx.Client.DeleteIssueAsync(issueKey, deleteSubtasks, cancellationToken);
                return ToolResult.DictResult(new { deleted = true, issue_key = issueKey });
            }
            catch (Exception exc)
            {
                return ToolResult.ErrorResult(exc);
            }
        }

        return ToolResult.DictResult(new
        {
            issue_key = issueKey,
            deleted = false,
            reason = "confirm=False",
            note = "Set confirm=true to delete. This is irreversible.",
        }, "result");
    }

    [McpServerTool(Name = "jira_transition_issue", Title = "Transition a Jira issue")]
    [Description("Move an issue through a workflow by transition id or target status name. If target_status is given, the server resolves it to a transition id first.")]
    public async Task<CallToolResult> TransitionIssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Exact transition id, e.g. '31'")] string? transitionId = null,
        [Description("Target status name, e.g. 'In Progress'")] string? targetStatus = null,
        [Description("Optional comment to attach to the transition")] string? comment = null,
        [Description("Required fields to set during transition")] System.Text.Json.Nodes.JsonObject? fields = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrEmpty(transitionId))
            {
                // use the explicit id
            }
            else if (!string.IsNullOrEmpty(targetStatus))
            {
                var transitionsObj = await _ctx.Client.GetTransitionsAsync(issueKey, cancellationToken);
                var transitions = transitionsObj?["transitions"] as JsonArray;
                var matches = (transitions ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Where(t => string.Equals(
                        ToolPayload.GetString(t, "name")?.Trim(),
                        targetStatus.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    var available = (transitions ?? new JsonArray())
                        .OfType<JsonObject>()
                        .Select(t => ToolPayload.GetString(t, "name"))
                        .Where(static n => n is not null);
                    return ToolResult.DictResult(new
                    {
                        issue_key = issueKey,
                        error = $"No transition to status '{targetStatus}'. Available: {string.Join(", ", available)}",
                    }, "result");
                }

                transitionId = ToolPayload.GetString(matches[0], "id");
            }
            else
            {
                return ToolResult.ErrorResult(new ArgumentException("Provide either transition_id or target_status"));
            }

            await _ctx.Client.TransitionIssueAsync(issueKey, transitionId!, FieldsToDict(fields), cancellationToken);
            if (!string.IsNullOrEmpty(comment))
            {
                await _ctx.Client.AddCommentAsync(issueKey, Adf.TextToAdf(comment), cancellationToken: cancellationToken);
            }

            return ToolResult.DictResult(new
            {
                issue_key = issueKey,
                transition_id = transitionId,
                transitioned = true,
            }, "result");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_transitions", Title = "Get available transitions for an issue")]
    [Description("Return the list of workflow transitions available for an issue.")]
    public async Task<CallToolResult> GetTransitionsAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var transitions = await _ctx.Client.GetTransitionsAsync(issueKey, cancellationToken);
            var list = transitions?["transitions"] as JsonArray ?? new JsonArray();
            return ToolResult.DictResult(list, "transitions");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_add_comment", Title = "Add a comment to an issue")]
    [Description("Post a comment to an issue, with optional visibility restricted to a group or role.")]
    public async Task<CallToolResult> AddCommentAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Comment body (plain text)")] string body,
        [Description("Visibility type: 'group' or 'role'")] string? visibility = null,
        [Description("Group name or role name for visibility")] string? visibilityValue = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _ctx.Client.AddCommentAsync(
                issueKey,
                Adf.TextToAdf(body),
                visibility,
                visibilityValue,
                cancellationToken);
            return ToolResult.DictResult(result, "comment");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_comments", Title = "Get comments for an issue")]
    [Description("Return comments on an issue with pagination.")]
    public async Task<CallToolResult> GetCommentsAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Maximum number of comments to return")] int maxResults = 50,
        [Description("Starting index for pagination")] int startAt = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var comments = await _ctx.Client.GetCommentsAsync(issueKey, maxResults, startAt, cancellationToken);
            var list = comments?["comments"] as JsonArray ?? new JsonArray();
            return ToolResult.DictResult(list, "comments");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_link_issues", Title = "Link two issues")]
    [Description("Create a link between two issues (blocks, relates to, etc.).")]
    public async Task<CallToolResult> LinkIssuesAsync(
        [Description("Inward issue key, e.g. PROJ-100")] string inwardIssueKey,
        [Description("Outward issue key, e.g. PROJ-200")] string outwardIssueKey,
        [Description("Link type name, e.g. Blocks, Relates to")] string linkType,
        [Description("Optional comment on the link")] string? comment = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _ctx.Client.LinkIssuesAsync(inwardIssueKey, outwardIssueKey, linkType, comment, cancellationToken);
            if (result is null)
            {
                return ToolResult.DictResult(new { linked = true }, "result");
            }

            return ToolResult.DictResult(result, "result");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_issue_links", Title = "Get links for an issue")]
    [Description("Return all link relationships for an issue.")]
    public async Task<CallToolResult> GetIssueLinksAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var issue = await _ctx.Client.GetIssueAsync(issueKey, fields: ["issuelinks"], cancellationToken: cancellationToken);
            var links = issue is JsonObject obj &&
                        obj["fields"] is JsonObject fields &&
                        fields["issuelinks"] is JsonArray linkArray
                ? linkArray
                : new JsonArray();
            return ToolResult.DictResult(links, "links");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_search_issues", Title = "Search issues with JQL")]
    [Description("Search issues using JQL. Uses the enhanced /rest/api/3/search/jql endpoint (POST by default; GET when the server's search_engine is 'get') — the only search API Jira Cloud still serves. The configured project allowlist is injected automatically to respect access boundaries. Pagination on this endpoint uses nextPageToken, so start_at is ignored. When fields is omitted, results include key, summary, status, and assignee; the returned payload is annotated with the effective scope when a project allowlist is configured.")]
    public async Task<CallToolResult> SearchIssuesAsync(
        [Description("JQL query string")] string jql,
        [Description("Maximum number of issues to return")] int maxResults = 50,
        [Description("Starting index for pagination (ignored for the enhanced search engine)")] int startAt = 0,
        [Description("Returned field names")] IReadOnlyList<string>? fields = null,
        [Description("Optional expand values")] IReadOnlyList<string>? expand = null,
        [Description("Pass true to treat 'fields' as field IDs/keys")] bool? fieldsByKeys = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scoped = _ctx.Client.WithProjectScope(jql);
            var data = await _ctx.Client.SearchIssuesAsync(
                scoped, maxResults, startAt, fields, expand, fieldsByKeys, cancellationToken);
            var payload = data as JsonObject ?? new JsonObject();
            var cleaned = SearchResultSanitizer.Sanitize(payload);
            _ctx.AttachScopeHint(cleaned);
            return ToolResult.DictResult(cleaned, "search");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_search_issues_jql_only", Title = "Search issues (compact)")]
    [Description("Search issues and return only key, summary, status, and assignee for quick retrieval. Uses the enhanced /rest/api/3/search/jql endpoint — the only search API Jira Cloud still serves. Project allowlist is still applied, and the payload is annotated with the effective scope when an allowlist is configured.")]
    public async Task<CallToolResult> SearchIssuesJqlOnlyAsync(
        [Description("JQL query string")] string jql,
        [Description("Maximum number of issues to return")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var scoped = _ctx.Client.WithProjectScope(jql);
            var data = await _ctx.Client.SearchIssuesAsync(
                scoped, maxResults, fields: ["summary", "status", "assignee"], cancellationToken: cancellationToken);
            var brevity = ToolPayload.ExtractBrevity(data);
            var payload = new JsonObject
            {
                ["total"] = brevity.Count,
                ["issues"] = brevity,
            };
            _ctx.AttachScopeHint(payload);
            return ToolResult.DictResult(payload, "result");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_issue_meta", Title = "Get issue metadata for a project")]
    [Description("Return the create-issue metadata (issue types and editable fields) for a project.")]
    public async Task<CallToolResult> GetIssueMetaAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _ctx.Client.GetCreateIssueMetaAsync(Jql.NormalizeProjectKey(projectKey), cancellationToken);
            return ToolResult.DictResult(data, "meta");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_project_meta", Title = "Get project metadata")]
    [Description("Return project detail including lead, components, and issue type info.")]
    public async Task<CallToolResult> GetProjectMetaAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _ctx.Client.GetProjectAsync(Jql.NormalizeProjectKey(projectKey), cancellationToken);
            return ToolResult.DictResult(data, "project");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    private static IDictionary<string, JsonNode?>? CustomFieldsToDict(JsonObject? customFields)
    {
        if (customFields is null)
        {
            return null;
        }

        return customFields.ToDictionary(static pair => pair.Key, static pair => pair.Value);
    }

    private static IDictionary<string, JsonNode?>? FieldsToDict(JsonObject? fields)
    {
        if (fields is null)
        {
            return null;
        }

        return fields.ToDictionary(static pair => pair.Key, static pair => pair.Value);
    }
}
