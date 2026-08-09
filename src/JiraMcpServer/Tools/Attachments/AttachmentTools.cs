using System.ComponentModel;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Jira.Errors;
using JiraMcpServer.Tools.Serde;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Tools.Attachments;

/// <summary>Attachment-related MCP tools. Files over 10 MiB are refused client-side before upload.</summary>
[McpServerToolType]
public sealed class AttachmentTools(JiraToolContext ctx)
{
    private readonly JiraToolContext _ctx = ctx;

    [McpServerTool(Name = "jira_add_attachment", Title = "Add an attachment")]
    [Description("Upload a local file as an attachment to an issue. Files over 10 MiB are refused.")]
    public async Task<CallToolResult> AddAttachmentAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        [Description("Absolute path to the local file")] string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = new FileInfo(filePath);
            if (!path.Exists)
            {
                return ToolResult.ErrorResult(
                    new JiraNotFoundError($"Attachment file not found: {filePath}", statusCode: 404));
            }

            if (path.Length > JiraDefaults.MaxAttachmentBytes)
            {
                return ToolResult.ErrorResult(
                    new JiraValidationError(
                        $"Attachment too large: {path.Length} bytes (max 10 MiB)",
                        statusCode: 400,
                        detail: new Dictionary<string, object?>
                        {
                            ["max_bytes"] = JiraDefaults.MaxAttachmentBytes,
                            ["size"] = path.Length,
                        }));
            }

            var result = await UploadAttachmentAsync(issueKey, path, cancellationToken);
            return ToolResult.DictResult(result, "attachment");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    [McpServerTool(Name = "jira_list_attachments", Title = "List attachments")]
    [Description("Return the attachments on an issue (filename, size, author, content URL).")]
    public async Task<CallToolResult> ListAttachmentsAsync(
        [Description("Issue key, e.g. PROJ-123")] string issueKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var attachments = await _ctx.Client.GetAttachmentMetadataAsync(issueKey, cancellationToken);
            return ToolResult.DictResult(ToolPayload.SummarizeAttachments(attachments), "attachments");
        }
        catch (Exception exc)
        {
            return ToolResult.ErrorResult(exc);
        }
    }

    private async Task<System.Text.Json.Nodes.JsonNode?> UploadAttachmentAsync(
        string issueKey, FileInfo path, CancellationToken cancellationToken)
    {
        // Jira's attachment endpoint needs multipart/form-data, which the JSON helper cannot
        // produce. The client's RequestRawAsync applies rate limiting, retry, and the live
        // Authorization header; we set the multipart body and the Atlassian CSRF token header here.
        var content = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(path.FullName, cancellationToken);
        var byteContent = new ByteArrayContent(fileBytes);
        byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(byteContent, "file", path.Name);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/attachments")
        {
            Content = content,
        };
        request.Headers.Add("X-Atlassian-Token", "no-check");

        using var response = await _ctx.Client.RequestRawAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw MapToJiraError(response);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await System.Text.Json.Nodes.JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static JiraError MapToJiraError(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var text = response.Content.ReadAsStringAsync().Result;
        return status switch
        {
            401 => new JiraAuthenticationError(text.Length > 0 ? text : "Authentication failed (401)", status),
            403 => new JiraPermissionError(text.Length > 0 ? text : "Permission denied (403)", status, warnings: ["You may need a different token or permission."]),
            404 => new JiraNotFoundError(text.Length > 0 ? text : "Resource not found (404)", status),
            400 => new JiraValidationError(text.Length > 0 ? text : "Bad request (400)", status),
            _ => new JiraApiError(text.Length > 0 ? text : $"Jira API error ({status})", status),
        };
    }
}
