using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Tools.Prompts;

/// <summary>
/// MCP prompts exposed by the server. Prompts guide the model through structured workflows
/// (bug report, sprint review, triage, daily standup) and are rendered lazily when a client
/// invokes <c>prompts/get</c>. Each returns instructions the model should follow plus the Jira
/// data-fetching tool names available to it; they are intentionally lightweight — the actual Jira
/// reads happen through the tools, keeping the prompt layer free of direct client access.
/// </summary>
[McpServerPromptType]
public sealed class PromptTemplates(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerPrompt(Name = "create_bug_report")]
    [Description("Guide the user through creating a structured bug report.")]
    public async Task<string> CreateBugReportAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        [Description("Issue summary/title")] string summary,
        [Description("Steps to reproduce the bug")] string? stepsToReproduce = null,
        [Description("Expected behavior")] string? expectedBehavior = null,
        [Description("Actual behavior")] string? actualBehavior = null,
        [Description("Severity, e.g. Critical, High, Medium, Low")] string? severity = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            "You are helping create a structured bug report in Jira.\n\n" +
            $"Project: {projectKey}\n" +
            $"Summary: {summary}\n" +
            $"Steps to reproduce: {stepsToReproduce ?? "(not provided)"}\n" +
            $"Expected behavior: {expectedBehavior ?? "(not provided)"}\n" +
            $"Actual behavior: {actualBehavior ?? "(not provided)"}\n" +
            $"Severity: {severity ?? "(not provided)"}\n\n" +
            "Draft a clear, reproducible description using the provided details, add " +
            "appropriate labels and priority, then call jira_create_issue with " +
            "issue_type='Bug'.");
    }

    [McpServerPrompt(Name = "sprint_review_summary")]
    [Description("Produce a sprint review summary from the given sprint.")]
    public async Task<string> SprintReviewSummaryAsync(
        [Description("Sprint id")] int sprintId,
        CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            $"Summarize the sprint {sprintId} for a sprint review.\n\n" +
            $"1. Call jira_get_sprint_issues(sprint_id={sprintId}).\n" +
            "2. Group the returned issues by status.\n" +
            "3. Report: total issues, completed, in progress, blocked, and a " +
            "bullet list of uncompleted work with owners.\n" +
            "4. Note any high-risk or overdue items for the demo.");
    }

    [McpServerPrompt(Name = "triage_issue")]
    [Description("Draft a triage recommendation for an issue.")]
    public async Task<string> TriageIssueAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(
            $"Help triage issue {issueKey}.\n\n" +
            $"1. Call jira_get_issue(issue_key={issueKey}).\n" +
            "2. Use jira_search_issues_jql_only with a JQL like " +
            "\"project = <project> AND summary ~ <keywords> ORDER BY created DESC\" " +
            "to find similar past issues.\n" +
            "3. Recommend a priority, component, and assignee based on the similarity " +
            "and severity, and suggest whether to link related issues.");
    }

    [McpServerPrompt(Name = "daily_standup_report")]
    [Description("Generate a daily standup report.")]
    public async Task<string> DailyStandupReportAsync(
        [Description("Project key, e.g. PROJ")] string projectKey,
        [Description("Optional sprint id to filter by")] int? sprintId = null,
        CancellationToken cancellationToken = default)
    {
        var hasSprint = sprintId is not null ? $" and optionally target sprint {sprintId}." : "";
        var sprintNote = sprintId is not null ? "Also filter to the given sprint if populated." : "";
        return await Task.FromResult(
            $"Generate a daily standup report for project {projectKey}{hasSprint}\n\n" +
            "1. Search for issues updated in the last 24 hours using " +
            $"jira_search_issues_jql_only with jql={StandupJql(projectKey)}\n" +
            "2. Group the issues by assignee.\n" +
            "3. For each person list: what they are working on, what is blocked, " +
            "what they completed yesterday.\n" +
            "4. Flag anything newly added or with a changed status.\n" +
            sprintNote);
    }

    /// <summary>Build the JQL for the 24h window, project-scoped.</summary>
    private static string StandupJql(string projectKey)
    {
        var escaped = projectKey.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"project = \"{escaped}\" AND updated >= -24h ORDER BY statuschanged DESC";
    }
}
