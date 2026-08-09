using System.ComponentModel;
using JiraMcpServer.Jira.Validators;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Tools.Resources;

/// <summary>
/// MCP resources exposed by the server. Templates use RFC 6570 syntax (<c>{projectKey}</c> etc.)
/// for path variables. Each handler returns plain text (a formatted representation of the Jira
/// payload) because MCP resources are read contexts rather than tool outputs. The four documented
/// resources are implemented so a client can address any issue, project, or transition path directly.
/// </summary>
[McpServerResourceType]
public sealed class JiraResources(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerResource(UriTemplate = "jira://projects", Name = "projects", Title = "Accessible Jira projects")]
    public async Task<string> ProjectsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var projects = await _ctx.Client.ListProjectsAsync(100, cancellationToken);
            var lines = new List<string>();
            if (projects is System.Text.Json.Nodes.JsonArray array)
            {
                foreach (var item in array.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    var key = ToolPayload.GetString(item, "key");
                    if (key is null)
                    {
                        continue;
                    }

                    lines.Add($"{key} - {ToolPayload.GetString(item, "name")} [{ToolPayload.GetString(item, "projectTypeKey")}]");
                }
            }

            return lines.Count > 0 ? string.Join("\n", lines) : "(no accessible projects)";
        }
        catch (Exception exc)
        {
            return $"(resource error: {exc.Message})";
        }
    }

    [McpServerResource(UriTemplate = "jira://project/{projectKey}/meta", Name = "project_meta", Title = "Project metadata")]
    public async Task<string> ProjectMetaAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        CancellationToken cancellationToken = default)
    {
        var meta = await _ctx.Client.GetCreateIssueMetaAsync(Jql.NormalizeProjectKey(projectKey), cancellationToken);
        return ToolResult.JsonDumps(meta);
    }

    [McpServerResource(UriTemplate = "jira://issue/{issueKey}", Name = "issue", Title = "Issue snapshot")]
    public async Task<string> IssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        var issue = await _ctx.Client.GetIssueAsync(issueKey, cancellationToken: cancellationToken);
        return ToolResult.JsonDumps(issue);
    }

    [McpServerResource(UriTemplate = "jira://issue/{issueKey}/transitions", Name = "transitions", Title = "Issue transitions")]
    public async Task<string> TransitionsAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        var transitions = await _ctx.Client.GetTransitionsAsync(issueKey, cancellationToken);
        var list = transitions is System.Text.Json.Nodes.JsonObject obj && obj["transitions"] is System.Text.Json.Nodes.JsonArray arr
            ? arr
            : new System.Text.Json.Nodes.JsonArray();
        return ToolResult.JsonDumps(list);
    }
}
