using System.ComponentModel;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Sprints;

/// <summary>Sprint / Board related MCP tools (Jira Agile REST API).</summary>
[McpServerToolType]
public sealed class SprintTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerTool(Name = "jira_list_boards", Title = "List boards")]
    [Description("List boards, optionally filtered by project key or type (scrum|kanban).")]
    public async Task<CallToolResult> ListBoardsAsync(
        [Description("Filter by project key")] string? projectKey = null,
        [Description("Board type: scrum or kanban")] string? boardType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var boards = await _ctx.Client.ListBoardsAsync(projectKey, boardType, cancellationToken);
            return ToolResult.DictResult(ToolPayload.SummarizeBoards(boards), "boards");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_list_sprints", Title = "List sprints")]
    [Description("List sprints in a board, optionally filtered by state.")]
    public async Task<CallToolResult> ListSprintsAsync(
        [Description("Board id, e.g. 2")] int boardId,
        [Description("Sprint state: active, closed, or future")] string? state = null,
        [Description("Maximum number of sprints to return")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sprints = await _ctx.Client.ListSprintsAsync(boardId, state, maxResults, cancellationToken);
            var list = sprints is System.Text.Json.Nodes.JsonObject obj
                ? obj["values"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray()
                : sprints as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            return ToolResult.DictResult(list, "sprints");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_sprint_issues", Title = "Get issues in a sprint")]
    [Description("Return the issues in a given sprint.")]
    public async Task<CallToolResult> GetSprintIssuesAsync(
        [Description("Sprint id, e.g. 15")] int sprintId,
        [Description("Maximum number of issues to return")] int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var issues = await _ctx.Client.GetSprintIssuesAsync(sprintId, maxResults, cancellationToken);
            var list = issues is System.Text.Json.Nodes.JsonObject obj
                ? obj["issues"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray()
                : issues as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            return ToolResult.DictResult(ToolPayload.SummarizeIssues(list), "issues");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_move_issues_to_sprint", Title = "Move issues to a sprint")]
    [Description("Move one or more issues into a sprint (Agile API).")]
    public async Task<CallToolResult> MoveIssuesToSprintAsync(
        [Description("Sprint id, e.g. 15")] int sprintId,
        [Description("Issue keys to move, e.g. ['PROJ-1', 'PROJ-2']")] IReadOnlyList<string> issueKeys,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _ctx.Client.MoveIssuesToSprintAsync(sprintId, issueKeys, cancellationToken);
            return ToolResult.DictResult(new
            {
                moved = issueKeys.Count,
                sprint_id = sprintId,
                issue_keys = issueKeys,
            }, "result");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }
}
