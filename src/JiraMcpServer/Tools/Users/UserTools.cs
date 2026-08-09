using System.ComponentModel;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Users;

/// <summary>User-related MCP tools.</summary>
[McpServerToolType]
public sealed class UserTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerTool(Name = "jira_search_users", Title = "Search users")]
    [Description("Search users by display name or email. Only returns accountId and displayName to honor privacy.")]
    public async Task<CallToolResult> SearchUsersAsync(
        [Description("Search query (name or partial email)")] string query,
        [Description("Maximum number of users to return")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var users = await _ctx.Client.SearchUsersAsync(query, maxResults, cancellationToken);
            return ToolResult.DictResult(ToolPayload.SanitizeUsers(users), "users");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_get_myself", Title = "Get current user")]
    [Description("Return details of the authenticated user (useful for debugging auth).")]
    public async Task<CallToolResult> GetMyselfAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _ctx.Client.GetMyselfAsync(cancellationToken);
            return ToolResult.DictResult(data, "myself");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }
}
