using System.ComponentModel;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Worklog;

/// <summary>Worklog-related MCP tools.</summary>
[McpServerToolType]
public sealed class WorklogTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerTool(Name = "jira_add_worklog", Title = "Add a worklog entry")]
    [Description("Log time spent on an issue (timeSpent format like '2h 30m').")]
    public async Task<CallToolResult> AddWorklogAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Time spent, e.g. '2h 30m' or '1d'")] string timeSpent,
        [Description("Worklog comment")] string? comment = null,
        [Description("ISO 8601 start time, e.g. 2024-01-01T10:00:00.000+0000")] string? started = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _ctx.Client.AddWorklogAsync(issueKey, timeSpent, comment, started, cancellationToken);
            return ToolResult.DictResult(result, "worklog");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_worklogs", Title = "Get worklogs")]
    [Description("Return all worklog entries for an issue.")]
    public async Task<CallToolResult> GetWorklogsAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var worklogs = await _ctx.Client.GetWorklogsAsync(issueKey, cancellationToken);
            var list = worklogs is System.Text.Json.Nodes.JsonObject obj
                ? obj["worklogs"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray()
                : worklogs as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            return ToolResult.DictResult(ToolPayload.SummarizeWorklogs(list), "worklogs");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }
}
