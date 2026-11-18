using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Client;

namespace JiraMcpServer.Tests;

/// <summary>Drives <see cref="ConfluenceClient"/> writes with a scripted handler and asserts the requests sent.</summary>
public class ConfluenceClientWriteTests
{
    [Fact]
    public async Task CreatePage_PostsStorageBodyWithSpaceAndParent()
    {
        var (client, handler) = CreateClient(_ => Json("""
            {"id":"123","title":"T","space":{"key":"PE"},"version":{"number":1},
             "_links":{"base":"https://example.com/wiki","webui":"/spaces/PE/pages/123/T"}}
            """));

        var result = await client.CreatePageAsync("PE", "T", "<p>hi</p>", parentId: "99");

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/wiki/rest/api/content", req.Path);
        var body = JsonNode.Parse(req.Body!)!.AsObject();
        Assert.Equal("page", body["type"]!.GetValue<string>());
        Assert.Equal("T", body["title"]!.GetValue<string>());
        Assert.Equal("PE", body["space"]!["key"]!.GetValue<string>());
        Assert.Equal("99", body["ancestors"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("<p>hi</p>", body["body"]!["storage"]!["value"]!.GetValue<string>());
        Assert.Equal("storage", body["body"]!["storage"]!["representation"]!.GetValue<string>());

        Assert.Equal("123", result["id"]!.GetValue<string>());
        Assert.Equal("PE", result["spaceKey"]!.GetValue<string>());
        Assert.Equal(1, result["versionNumber"]!.GetValue<long>());
        Assert.Equal("https://example.com/wiki/spaces/PE/pages/123/T", result["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task CreatePage_OmitsAncestorsWhenNoParent()
    {
        var (client, handler) = CreateClient(_ => Json("""{"id":"1","title":"T"}"""));

        var result = await client.CreatePageAsync("PE", "T", "");

        var body = JsonNode.Parse(handler.Requests[0].Body!)!.AsObject();
        Assert.False(body.ContainsKey("ancestors"));
        Assert.Equal("PE", result["spaceKey"]!.GetValue<string>()); // falls back to the requested space
    }

    [Fact]
    public async Task UpdatePage_ReadsVersionThenPutsNextVersionAndKeepsTitle()
    {
        var (client, handler) = CreateClient(req => req.Method == HttpMethod.Get
            ? Json("""{"id":"123","type":"page","title":"Old title","space":{"key":"PE"},"version":{"number":7},"body":{"storage":{"value":"<p>old</p>"}}}""")
            : Json("""{"id":"123","title":"Old title","space":{"key":"PE"},"version":{"number":8}}"""));

        var result = await client.UpdatePageAsync("123", "<p>new</p>", versionMessage: "via MCP");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Contains("expand=body.storage%2Cversion%2Cspace", handler.Requests[0].Path);

        var put = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("/wiki/rest/api/content/123", put.Path);
        var body = JsonNode.Parse(put.Body!)!.AsObject();
        Assert.Equal("Old title", body["title"]!.GetValue<string>());
        Assert.Equal(8, body["version"]!["number"]!.GetValue<long>());
        Assert.Equal("via MCP", body["version"]!["message"]!.GetValue<string>());
        Assert.Equal("<p>new</p>", body["body"]!["storage"]!["value"]!.GetValue<string>());

        Assert.Equal(7, result["previousVersion"]!.GetValue<long>());
        Assert.Equal(8, result["versionNumber"]!.GetValue<long>());
        Assert.Equal("replace", result["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdatePage_AppendKeepsExistingBodyAndNewTitleWins()
    {
        var (client, handler) = CreateClient(req => req.Method == HttpMethod.Get
            ? Json("""{"id":"5","type":"page","title":"Old","version":{"number":1},"body":{"storage":{"value":"<p>old</p>"}}}""")
            : Json("""{"id":"5","title":"New","version":{"number":2}}"""));

        var result = await client.UpdatePageAsync("5", "<p>more</p>", title: "New", append: true);

        var body = JsonNode.Parse(handler.Requests[1].Body!)!.AsObject();
        Assert.Equal("New", body["title"]!.GetValue<string>());
        Assert.Equal("<p>old</p><p>more</p>", body["body"]!["storage"]!["value"]!.GetValue<string>());
        Assert.False(body["version"]!.AsObject().ContainsKey("message"));
        Assert.Equal("append", result["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdatePage_NotFoundSurfacesAndNoPutIsSent()
    {
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"No content found"}""", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAnyAsync<Exception>(() => client.UpdatePageAsync("404", "<p>x</p>"));
        Assert.Single(handler.Requests);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (ConfluenceClient Client, ScriptedHandler Handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var settings = new CompiledConfig
        {
            JiraBaseUrl = "https://example.com",
            AuthMethod = "bearer",
            ApiToken = "test-token",
            SearchEngine = "jql",
            RateLimit = 100,
        };
        var handler = new ScriptedHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        return (new ConfluenceClient(new JiraClient(settings, http, maxRetries: 0)), handler);
    }

    private sealed record Captured(HttpMethod Method, string Path, string? Body);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(request.Method, request.RequestUri!.PathAndQuery, body));
            return respond(request);
        }
    }
}
