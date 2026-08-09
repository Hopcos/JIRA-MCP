using System.Text.Json;
using System.Text.Json.Nodes;
using JiraMcpServer.Jira.Errors;
using ModelContextProtocol.Protocol;

namespace JiraMcpServer.Tools.Serde;

/// <summary>
/// Helpers for serializing tool arguments, Jira payloads, and results. These are the only places
/// that construct <see cref="CallToolResult"/> and know the JSON formatting convention, so the
/// handlers stay small and tests have a single dependency to mock.
/// </summary>
public static class ToolResult
{
    /// <summary>Stable JSON serialization used for tool outputs (compact, ASCII-safe where possible).</summary>
    public static string JsonDumps(object? value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Build an <c>isError=true</c> <see cref="CallToolResult"/> for any handler failure.
    /// A 401 is treated specially: the text hints at token rotation, matching the
    /// reauthorization-required pattern many MCP clients recognize.
    /// </summary>
    public static CallToolResult ErrorResult(Exception error, IReadOnlyList<string>? warnings = null)
    {
        string message;
        var combinedWarnings = new List<string>(warnings ?? Array.Empty<string>());

        if (error is JiraError jiraError)
        {
            message = jiraError.ToString();
            combinedWarnings.InsertRange(0, jiraError.Warnings);
            if (jiraError.StatusCode == 401)
            {
                if (message.Length > 0)
                {
                    message += " ";
                }

                message +=
                    "The API token is invalid or expired. Rotate the token and restart the server " +
                    "(or the parent process) with the new credential.";
            }
        }
        else
        {
            message = $"Internal error: {error.Message}";
        }

        if (combinedWarnings.Count > 0)
        {
            message += "\n" + string.Join("\n", combinedWarnings.Select(static w => $"[warn] {w}"));
        }

        return TextResult(message, isError: true);
    }

    /// <summary>Build a success <see cref="CallToolResult"/> wrapping plain text.</summary>
    public static CallToolResult TextResult(string text, bool isError = false) =>
        new()
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = isError,
        };

    /// <summary>Build a success <see cref="CallToolResult"/> from a JSON-serializable value.</summary>
    public static CallToolResult DictResult(object? data, string? label = null, IEnumerable<JsonObject>? annotations = null)
    {
        object payload = label is null ? data! : new Dictionary<string, object?> { [label] = data };
        return TextResult(JsonDumps(payload));
    }
}
