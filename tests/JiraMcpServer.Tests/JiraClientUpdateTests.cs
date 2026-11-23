using System.Net;
using System.Text.Json.Nodes;
using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Client;

namespace JiraMcpServer.Tests;

/// <summary>
/// Regression tests for the "The node already has a parent." bug in <see cref="JiraClient"/>.
///
/// The MCP tools parse the tool's <c>fields</c> argument into a <see cref="JsonObject"/>. The values
/// of that object are still attached to it as their parent, so re-parenting them into a freshly built
/// request-body object used to throw <c>InvalidOperationException</c>. These tests drive the client
/// with a capturing handler so the body is asserted rather than a network call being made.
/// </summary>
public class JiraClientUpdateTests
{
    [Fact]
    public async Task UpdateIssueAsync_DoesNotThrowWhenFieldsComeFromParsedJson()
    {
        // Simulate the MCP tool path: fields arrive as a JsonObject (description is an ADF document).
        var fields = JsonNode.Parse("""
            {
              "description": { "type": "doc", "version": 1, "content": [ { "type": "paragraph", "content": [ { "type": "text", "text": "English description" } ] } ] },
              "summary": "New title"
            }
            """)!.AsObject();
        var dict = fields.ToDictionary(static p => p.Key, static p => p.Value);

        var (client, handler) = CreateClient();
        await client.UpdateIssueAsync("PROJ-1", dict, notifyUsers: null);

        Assert.NotNull(handler.LastBody);
        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        var sent = body["fields"]!.AsObject();
        Assert.Equal("New title", sent["summary"]!.GetValue<string>());
        Assert.Equal("doc", sent["description"]!["type"]!.GetValue<string>());
        Assert.Equal("English description", sent["description"]!["content"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateIssueAsync_HandlesSingleStringField()
    {
        // A single plain string field triggers the same re-parenting path.
        var fields = JsonNode.Parse("""{ "summary": "New title" }""")!.AsObject();
        var dict = fields.ToDictionary(static p => p.Key, static p => p.Value);

        var (client, handler) = CreateClient();
        await client.UpdateIssueAsync("PROJ-1", dict, notifyUsers: null);

        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("New title", body["fields"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task TransitionIssueAsync_DoesNotThrowWhenFieldsComeFromParsedJson()
    {
        var fields = JsonNode.Parse("""{ "summary": "New title" }""")!.AsObject();
        var dict = fields.ToDictionary(static p => p.Key, static p => p.Value);

        var (client, handler) = CreateClient();
        await client.TransitionIssueAsync("PROJ-1", "31", dict);

        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        Assert.Equal("31", body["transition"]!["id"]!.GetValue<string>());
        Assert.Equal("New title", body["fields"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreateIssueAsync_DoesNotThrowWhenCustomFieldsComeFromParsedJson()
    {
        var customFields = JsonNode.Parse("""{ "customfield_10010": "value" }""")!.AsObject();
        var custom = customFields.ToDictionary(static p => p.Key, static p => p.Value);

        var (client, handler) = CreateClient();
        await client.CreateIssueAsync(
            "PROJ",
            "Summary",
            "Task",
            descriptionAdf: null,
            customFields: custom);

        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        // Jira requires every field under a top-level "fields" key (regression: bare map => 400
        // "project: Specify a valid project ID or key").
        Assert.False(body.ContainsKey("project"));
        var sent = body["fields"]!.AsObject();
        Assert.Equal("value", sent["customfield_10010"]!.GetValue<string>());
        Assert.Equal("PROJ", sent["project"]!["key"]!.GetValue<string>());
        Assert.Equal("Summary", sent["summary"]!.GetValue<string>());
        Assert.Equal("Task", sent["issuetype"]!["name"]!.GetValue<string>());
    }

    private static (JiraClient Client, CapturingHandler Handler) CreateClient()
    {
        var settings = new CompiledConfig
        {
            JiraBaseUrl = "https://example.com",
            AuthMethod = "bearer",
            ApiToken = "test-token",
            SearchEngine = "jql",
            RateLimit = 100,
        };
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        var client = new JiraClient(settings, http, maxRetries: 0);
        return (client, handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
