using System.Text.Json.Nodes;
using JiraMcpServer.Jira.Formatters;

namespace JiraMcpServer.Tests;

public class ConfluenceAdfTests
{
    [Fact]
    public void RendersParagraphAndText()
    {
        var doc = new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "paragraph",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "Hello world" }),
                }),
        };
        Assert.Equal("Hello world", ConfluenceAdf.AdfToText(doc));
    }

    [Fact]
    public void RendersTableAsPipes()
    {
        var doc = new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "table",
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "tableRow",
                            ["content"] = new JsonArray(
                                Cell("Endpoint"), Cell("http://pe.local")),
                        }),
                }),
        };
        Assert.Equal("Endpoint | http://pe.local", ConfluenceAdf.AdfToText(doc));
    }

    [Fact]
    public void RendersBulletListWithHyphens()
    {
        var doc = new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "bulletList",
                    ["content"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "listItem",
                            ["content"] = new JsonArray(
                                new JsonObject
                                {
                                    ["type"] = "paragraph",
                                    ["content"] = new JsonArray(
                                        new JsonObject { ["type"] = "text", ["text"] = "first" }),
                                }),
                        }),
                }),
        };
        Assert.Equal("- first", ConfluenceAdf.AdfToText(doc));
    }

    [Fact]
    public void RendersMediaAltText()
    {
        var doc = new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "media", ["attrs"] = new JsonObject { ["alt"] = "screenshot" } }),
        };
        Assert.Equal("[media] screenshot", ConfluenceAdf.AdfToText(doc));
    }

    [Fact]
    public void HandlesEmptyOrNull()
    {
        Assert.Equal("", ConfluenceAdf.AdfToText(null));
        Assert.Equal("", ConfluenceAdf.AdfToText(""));
    }

    private static JsonObject Cell(string text) => new()
    {
        ["type"] = "tableCell",
        ["content"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = text }),
            }),
    };
}
