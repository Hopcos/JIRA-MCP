using System.ComponentModel;
using JiraMcpServer.Jira.Validators;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Projects;

/// <summary>Project-related MCP tools.</summary>
[McpServerToolType]
public sealed class ProjectTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerTool(Name = "jira_list_projects", Title = "List projects")]
    [Description("Return all projects the authenticated user can access.")]
    public async Task<CallToolResult> ListProjectsAsync(
        [Description("Maximum number of projects to return")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var projects = await _ctx.Client.ListProjectsAsync(maxResults, cancellationToken);
            return ToolResult.DictResult(ToolPayload.SummarizeProjects(projects), "projects");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_project", Title = "Get a project")]
    [Description("Return full details for a single project.")]
    public async Task<CallToolResult> GetProjectAsync(
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

    [McpServerTool(Name = "jira_get_project_versions", Title = "Get project versions")]
    [Description("Return all release versions for a project.")]
    public async Task<CallToolResult> GetProjectVersionsAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var versions = await _ctx.Client.GetProjectVersionsAsync(Jql.NormalizeProjectKey(projectKey), cancellationToken);
            return ToolResult.DictResult(versions, "versions");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }
}
